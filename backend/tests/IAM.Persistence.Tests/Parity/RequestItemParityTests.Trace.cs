using System.Diagnostics;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Request.Infrastructure.Persistence;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 5b (DL-094), end to end on both engines and a real broker: a request is submitted, relayed, its approval
/// started by the workflow consumer, decided and named by its owner in separate calls, the authorisation relayed and taken by
/// the provisioning consumer, run on the mock AD connector by the runner and recorded. One correlation id runs through every
/// audit record, in order; repeats change nothing. The mock connector is never production-ready (rule 10).
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task OneRequestIsTraceableFromSubmissionToTheTarget()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));

        // 1. Submitted in a web request's trace.
        RequestItemView item;
        string correlation;
        using (var web = new Activity("test.submit").Start())
        {
            correlation = web.TraceId.ToHexString();
            await using var scope = Service(world.Beneficiary, route);
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), Ct);
        }

        await using (var request = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString))
        {
            (await request.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id, Ct)).CorrelationId.ShouldBe(correlation);
        }

        // 2. Relayed by the request module and taken by the workflow consumer: the approval starts under the same id.
        await RelayAsync<RequestDbContext>(() => TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString, Interceptor("system:request-relay")), broker.Request, RequestEvents.Source);
        var submitted = await TakeAsync("iam.workflow.request_items", RequestItemSubmitted.MessageType, item.Id);
        await using (var services = WorkflowConsumerServices())
        {
            (await Consumer<RequestItemApprovals>(services, broker.Workflow).DispatchAsync(submitted.Exchange, submitted.RoutingKey, submitted.BasicProperties, submitted.Body, Ct))
                .ShouldBe(DeliveryDisposition.Ack);
        }

        // 3. The owner decides, names the account and completes the task, each in a call of its own.
        var (workflow, context) = Workflow(world.EntitlementOwner);
        await using (context)
        {
            var instance = (await workflow.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            using (new Activity("test.decide").Start())
            {
                instance = await workflow.DecideAsync(item.Id, instance.Version, Guid.CreateVersion7(), instance.Stages.Single(s => s.Kind == "Decision").StageId, true, null, Ct);
            }

            var action = instance.Stages.Single(s => s.Kind == "Action").StageId;
            using (new Activity("test.name").Start())
            {
                await using var scope = Service(world.EntitlementOwner, route);
                await scope.Service.GiveAccountNameAsync(item.Id, action, name, Ct);
            }

            using (new Activity("test.complete").Start())
            {
                instance = (await workflow.FindAsync(item.Id, Ct)).ShouldNotBeNull();
                (await workflow.CompleteAsync(item.Id, instance.Version, Guid.CreateVersion7(), action, "Created", Ct)).State.ShouldBe("Approved");
            }
        }

        // 4. Relayed by the workflow and taken by the provisioning consumer (with its own broker user): checked and planned.
        await RelayAsync<WorkflowDbContext>(() => TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, Interceptor("system:workflow-relay")), broker.Workflow, WorkflowEvents.Source);
        var authorized = await TakeAsync("iam.provisioning.approvals", ApprovalAuthorized.MessageType, item.Id);
        var closed = await TakeAsync("iam.provisioning.approvals", ApprovalClosed.MessageType, item.Id);
        authorized.BasicProperties.CorrelationId.ShouldBe(correlation, "the workflow sends with the request's id, not the decider's");
        await using (var services = ProvisioningConsumerServices(world))
        {
            var consumer = Consumer<AuthorizedApprovals>(services, broker.Provisioning);
            foreach (var message in new[] { authorized, closed, authorized })
            {
                (await consumer.DispatchAsync(message.Exchange, message.RoutingKey, message.BasicProperties, message.Body, Ct)).ShouldBe(DeliveryDisposition.Ack);
            }
        }

        var (planned, operations) = await PlannedAsync(item.Id);
        planned.State.ShouldBe(ProvisioningState.Planned);
        planned.CorrelationId.ShouldBe(correlation);

        // 5. Run on the mock AD connector (its directory has the container and the group), verified, then recorded.
        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, new InMemoryOperationLedger(), "DC=corp,DC=test");
        mock.AddContainer("Users");
        var group = operations[1].RelatedId.ShouldNotBeNull();
        mock.AddGroup(group["CN=".Length..group.IndexOf(',', StringComparison.Ordinal)], mock.AddContainer("Groups"));
        (await RunAsync(item.Id, new ConnectorTarget(mock))).ShouldBeTrue();

        var (done, steps) = await PlannedAsync(item.Id);
        done.State.ShouldBe(ProvisioningState.Succeeded);
        steps.ShouldAllBe(s => s.State == ProvisioningState.Succeeded);
        var dn = $"CN={name},{AccountContainer}";
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, dn), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(true);
        account.Links.Select(l => l.Id).ShouldContain(l => string.Equals(l, group, StringComparison.OrdinalIgnoreCase));
        await using (var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString))
        {
            var recorded = (await new AccountDirectory(accounts).FindAsync(done.RecordedAccountId!.Value, Ct)).ShouldNotBeNull();
            recorded.ShouldSatisfyAllConditions(r => r.Name.ShouldBe(name), r => r.TargetId.ShouldBe(dn), r => r.Verified.ShouldBeTrue(), r => r.Owner.ShouldBe(world.Beneficiary));
        }

        // 6. One correlation id, in order, from submission to the recorded account; the decider's calls are only causes.
        var trail = await TrailAsync(correlation);
        trail.Select(t => t.Action).ShouldContain("workflow.approval.approvers_supplied");
        string[] expected =
        [
            RequestAuditActions.Submitted, "workflow.approval.started", "workflow.approval.decided", RequestAuditActions.AccountNamed,
            "workflow.approval.completed", ProvisioningAuditActions.Planned, ProvisioningAuditActions.StepDone, ProvisioningAuditActions.StepDone,
            ProvisioningAuditActions.StepDone, AccountProvisioningService.Created, ProvisioningAuditActions.Succeeded,
        ];
        trail.Select(t => t.Action).Where(expected.Contains).ShouldBe(expected);
        trail.Single(t => t.Action == "workflow.approval.decided").Cause.ShouldNotBeNull().ShouldNotBe(correlation);

        // Again: nothing is planned, run or recorded twice.
        (await RunAsync(item.Id, new ConnectorTarget(mock))).ShouldBeFalse();
        (await TrailAsync(correlation)).Count.ShouldBe(trail.Count);
    }

    // ---- Trace helpers -----------------------------------------------------------------------------------------------------

    private static AuditingSaveChangesInterceptor Interceptor(string actor) => new(TimeProvider.System, new FixedActor(actor));

    private async Task<List<(string Action, string? Cause)>> TrailAsync(string correlation)
    {
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        return [.. (await audit.Events.AsNoTracking().Where(e => e.CorrelationId == correlation).OrderBy(e => e.Seq)
            .Select(e => new { e.Action, e.CausationId }).ToListAsync(Ct)).Select(e => (e.Action, e.CausationId))];
    }

    /// <summary>Sends every waiting outbox message of a module, as its relay does, with the module's own broker user.</summary>
    private static async Task RelayAsync<TContext>(Func<TContext> context, RabbitMqSettings user, string source)
        where TContext : IamDbContext
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => context());
        await using var provider = services.BuildServiceProvider();
        await using var publisher = new RabbitMqPublisher(user, source);
        var relay = new OutboxRelay<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), publisher, new OutboxRelaySettings(batchSize: 500), TimeProvider.System, NullLogger<OutboxRelay<TContext>>.Instance);
        for (var pass = 0; pass < 20 && (await relay.RunOnceAsync(Ct)).Sent > 0; pass++)
        {
        }
    }

    /// <summary>The first message of a type about an item from a queue, as the broker delivers it (others are dropped).</summary>
    private async Task<BasicGetResult> TakeAsync(string queue, string type, Guid itemId)
    {
        await using var connection = await broker.ConnectAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var text = itemId.ToString("D");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: true, Ct);
            if (message is null)
            {
                await Task.Delay(100, Ct);
                continue;
            }

            if (message.BasicProperties.Type == type && System.Text.Encoding.UTF8.GetString(message.Body.Span).Contains(text, StringComparison.Ordinal))
            {
                return message;
            }
        }

        throw new TimeoutException($"No {type} for the item arrived on {queue}.");
    }

    private ServiceProvider WorkflowConsumerServices()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new WorkflowHolder(Workflow()));
        services.AddScoped(sp => new RequestItemApprovals(sp.GetRequiredService<WorkflowHolder>().Service));
        return services.BuildServiceProvider();
    }

    private ServiceProvider ProvisioningConsumerServices(World world)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Provisioning(Bound(world)));
        services.AddScoped(sp => sp.GetRequiredService<ProvisioningScope>().Handler);
        return services.BuildServiceProvider();
    }

    private static RabbitMqConsumer<T> Consumer<T>(ServiceProvider services, RabbitMqSettings user)
        where T : class, IMessageHandler =>
        new(services.GetRequiredService<IServiceScopeFactory>(), user, new ConsumerSettings(reconnectDelaySeconds: 1), NullLogger<RabbitMqConsumer<T>>.Instance);

    private sealed class WorkflowHolder((ApprovalWorkflowService Service, IAsyncDisposable Context) workflow) : IDisposable
    {
        public ApprovalWorkflowService Service => workflow.Service;

        public void Dispose() => ((IDisposable)workflow.Context).Dispose();
    }
}
