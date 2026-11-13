using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Domain.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace IAM.Api.Tests;

/// <summary>
/// P0-013 slice 3, R-023: every endpoint names the action it needs, and the check runs on the server per request. Uses a
/// fake evaluator or store; the real store is covered by the parity tests.
/// </summary>
public sealed class EndpointPermissionTests
{
    /// <summary>The only endpoints that may skip the permission check, reviewed one by one.</summary>
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal) { "/health/live" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryEndpointNamesAKnownActionOrIsReviewedAnonymous()
    {
        using var host = new ApiTestHost();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.ShouldNotBeEmpty();
        var problems = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "?";
            var actions = endpoint.Metadata.OfType<IAuthorizationRequirementData>()
                .SelectMany(d => d.GetRequirements())
                .OfType<PermissionRequirement>()
                .Select(r => r.Action)
                .ToList();
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                if (!Anonymous.Contains(route))
                {
                    problems.Add($"{route} is anonymous but not on the reviewed list");
                }

                continue;
            }

            if (actions.Count == 0)
            {
                problems.Add($"{route} names no action (RequirePermission)");
            }

            problems.AddRange(actions.Where(a => !ActionCatalog.IsKnown(a)).Select(a => $"{route} names unknown action {a}"));
        }

        problems.ShouldBeEmpty();
    }

    /// <summary>Each endpoint's action, by name: a change here is a security change and must be reviewed.</summary>
    [Fact]
    public void EachEndpointNamesExactlyTheReviewedAction()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Identity.GetCurrentSubject"] = "identity.self.read",
            ["Administration.GetOwnPermissions"] = "administration.permissions.read-own",
            ["Administration.GetRoles"] = "administration.roles.read",
            ["Administration.GetChange"] = "administration.changes.read",
            ["Administration.ProposeChange"] = "administration.changes.propose",
            ["Administration.ApproveChange"] = "administration.changes.approve",
            ["Administration.RejectChange"] = "administration.changes.approve",
            ["Administration.WithdrawChange"] = "administration.changes.propose",
            ["Identity.GetAttributeDraft"] = "identity.attributes.read",
            ["Identity.RegisterAttribute"] = "identity.attributes.manage",
            ["Identity.UpdateAttributeDraft"] = "identity.attributes.manage",
            ["Identity.StartAttributeDraft"] = "identity.attributes.manage",
            ["Identity.ProposeAttributePublish"] = "identity.attributes.manage",
            ["Identity.GetAttributePublishRequest"] = "identity.attributes.read",
            ["Identity.ApproveAttributePublish"] = "identity.attributes.approve",
            ["Identity.RejectAttributePublish"] = "identity.attributes.approve",
            ["Identity.WithdrawAttributePublish"] = "identity.attributes.manage",
        };
        using var host = new ApiTestHost();
        var routed = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        routed.Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>() is null).Select(e => e.RoutePattern.RawText)
            .ShouldBeEmpty("every protected endpoint has a name, so this table can't skip one");
        var actual = routed
            .ToDictionary(
                e => e.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName,
                e => string.Join(",", e.Metadata.OfType<IAuthorizationRequirementData>().SelectMany(d => d.GetRequirements()).OfType<PermissionRequirement>().Select(r => r.Action)),
                StringComparer.Ordinal);
        actual.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task AnAllowedCallerReachesTheEndpointAndTheCheckSeesTheirSubjectAndAction()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = HostWith(evaluator);
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken("subject-123"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        evaluator.Calls.ShouldBe([("subject-123", "identity.self.read")]);
    }

    [Fact]
    public async Task ADeniedCallerGets403AndNoBody()
    {
        using var host = HostWith(new FakeEvaluator(allow: false));
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NoTokenIs401BeforeAnyPermissionCheck()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = HostWith(evaluator);
        using var response = await GetAsync(host, "/api/identity/me", token: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        evaluator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenWithoutASubjectIsNeverAllowed()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = HostWith(evaluator);
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken(subject: null));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        evaluator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenWithAnEmptySubjectIsNeverAllowedEvenIfTheEvaluatorWouldBe()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = HostWith(evaluator);
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken(subject: string.Empty));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        evaluator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenWithAnEmptySubjectIsNeverAllowed()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = HostWith(evaluator);
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken(subject: string.Empty));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        evaluator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADamagedStoreIs503WithNoDetailsNeverAGrant()
    {
        using var host = HostWith(new FakeEvaluator(allow: true) { Failure = new AccessStoreIntegrityException("hash", "stored value secret-role-x") });
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("secret-role-x");
        body.ShouldNotContain("subject-123");
        body.ShouldNotContain("hash");
    }

    /// <summary>Reads are limited per person before the permission check runs, and each person has their own allowance.</summary>
    [Fact]
    public async Task ReadsAreRateLimitedPerPersonBeforeThePermissionCheck()
    {
        var evaluator = new FakeEvaluator(allow: true);
        using var host = new ApiTestHost(
            configureServices: services =>
            {
                services.RemoveAll<IPermissionEvaluator>();
                services.AddSingleton<IPermissionEvaluator>(evaluator);
            },
            settings: new Dictionary<string, string> { [IamRateLimits.RequestsPerMinutePerPersonKey] = "2" });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken("busy-person"));
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        evaluator.Calls.Count.ShouldBe(2, "the refused request never reached the permission check");

        using var other = await GetAsync(host, "/api/identity/me", host.CreateValidToken("someone-else"));
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADenialIsRecordedWithThePersonActionAndRouteOnly()
    {
        var recorder = new FakeRecorder();
        using var host = new ApiTestHost(configureServices: services =>
        {
            services.RemoveAll<IPermissionEvaluator>();
            services.AddSingleton<IPermissionEvaluator>(new FakeEvaluator(allow: false));
            services.RemoveAll<IPermissionDenialRecorder>();
            services.AddSingleton<IPermissionDenialRecorder>(recorder);
        });
        using var denied = await GetAsync(host, "/api/identity/me", host.CreateValidToken("nosy-person"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        recorder.Records.ShouldBe([("nosy-person", "identity.self.read", "/api/identity/me")]);
    }

    [Fact]
    public async Task AllowedRequestsAreNotRecordedAndARecorderFailureStillRefuses()
    {
        var recorder = new FakeRecorder();
        using (var host = new ApiTestHost(configureServices: services =>
        {
            services.RemoveAll<IPermissionEvaluator>();
            services.AddSingleton<IPermissionEvaluator>(new FakeEvaluator(allow: true));
            services.RemoveAll<IPermissionDenialRecorder>();
            services.AddSingleton<IPermissionDenialRecorder>(recorder);
        }))
        {
            using var allowed = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
            recorder.Records.ShouldBeEmpty();
        }

        using var failing = new ApiTestHost(configureServices: services =>
        {
            services.RemoveAll<IPermissionEvaluator>();
            services.AddSingleton<IPermissionEvaluator>(new FakeEvaluator(allow: false));
            services.RemoveAll<IPermissionDenialRecorder>();
            services.AddSingleton<IPermissionDenialRecorder>(new FakeRecorder { Failure = new InvalidOperationException("audit store down") });
        });
        using var denied = await GetAsync(failing, "/api/identity/me", failing.CreateValidToken());
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OtherFailuresAre500WithNothingFromTheException()
    {
        using var host = HostWith(new FakeEvaluator(allow: true) { Failure = new InvalidOperationException("duplicate key (alice, iam-manager)") });
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("alice");
        body.ShouldNotContain("InvalidOperationException");
    }

    /// <summary>The exception's message (which can hold people and role codes) never reaches the logs; its type does.</summary>
    [Fact]
    public async Task OtherFailuresAreLoggedByTypeOnly()
    {
        var logs = new CapturingLoggerProvider();
        using var host = new ApiTestHost(configureServices: services =>
        {
            services.RemoveAll<IPermissionEvaluator>();
            services.AddSingleton<IPermissionEvaluator>(new FakeEvaluator(allow: true) { Failure = new InvalidOperationException("duplicate key (alice, iam-manager)") });
            services.AddSingleton<ILoggerProvider>(logs);
        });
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        logs.Text.ShouldContain("System.InvalidOperationException");
        logs.Text.ShouldNotContain("alice");
    }

    [Fact]
    public async Task AnAccessFailureKeepsItsStatusWhenTheCallerRefusesJson()
    {
        using var host = HostWith(new FakeEvaluator(allow: true) { Failure = new AccessStoreIntegrityException("hash", "x") });
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/identity/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateValidToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ErrorResponsesCarryTheSecurityHeadersToo()
    {
        using var host = HostWith(new FakeEvaluator(allow: true) { Failure = new AccessStoreIntegrityException("hash", "x") });
        using var response = await GetAsync(host, "/api/identity/me", host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedWriteBodiesAre413WithOrWithoutADeclaredLength(bool chunked)
    {
        using var host = HostWith(new FakeEvaluator(allow: true));
        using var client = host.CreateClient();
        var json = "{\"kind\":\"AssignRole\",\"subject\":\"x\",\"roleCode\":\"auditor\",\"reason\":\"" + new string('r', 20_000) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/administration/changes", UriKind.Relative))
        {
            Content = chunked
                ? new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                : new StringContent(json, System.Text.Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateValidToken());
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task OwnPermissionsShowOnlyTheCallersRolesAndActions()
    {
        var model = AccessModel.Defaults().Bootstrap("subject-123", "someone-else");
        using var host = new ApiTestHost(configureServices: services =>
        {
            services.RemoveAll<IAccessAdministration>();
            services.AddScoped<IAccessAdministration>(_ => new ModelOnlyAccess(model));
        });
        using var response = await GetAsync(host, "/api/administration/permissions/me", host.CreateValidToken("subject-123"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OwnPermissions>(Ct);
        body.ShouldNotBeNull();
        body.Roles.ShouldBe([AccessModel.IamAdministrator]);
        body.Actions.ShouldContain(ActionCatalog.ChangesPropose);
        body.Actions.ShouldNotContain(ActionCatalog.ChangesApprove);
        body.Actions.ShouldBe(body.Actions.Order(StringComparer.Ordinal));
        body.ModelVersion.ShouldBe(model.Version);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("someone-else");
    }

    private static ApiTestHost HostWith(IPermissionEvaluator evaluator) => new(configureServices: services =>
    {
        services.RemoveAll<IPermissionEvaluator>();
        services.AddSingleton(evaluator);
    });

    private static async Task<HttpResponseMessage> GetAsync(ApiTestHost host, string path, string? token)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed record OwnPermissions(List<string> Roles, List<string> Actions, long ModelVersion);

    private sealed class FakeEvaluator(bool allow) : IPermissionEvaluator
    {
        private readonly List<(string, string)> _calls = [];

        public Exception? Failure { get; init; }

        public IReadOnlyList<(string, string)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add((subject, action));
            }

            return Failure is null ? Task.FromResult(allow) : Task.FromException<bool>(Failure);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public string Text => string.Join((char)0x0A, _lines);

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }

    private sealed class FakeRecorder : IPermissionDenialRecorder
    {
        private readonly List<(string, string, string?)> _records = [];

        public Exception? Failure { get; init; }

        public IReadOnlyList<(string, string, string?)> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public Task RecordAsync(string subject, string action, string? routeTemplate, CancellationToken cancellationToken)
        {
            lock (_records)
            {
                _records.Add((subject, action, routeTemplate));
            }

            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class ModelOnlyAccess(AccessModel model) : IAccessAdministration
    {
        public Task<AccessModel> CurrentModelAsync(CancellationToken cancellationToken) => Task.FromResult(model);

        public Task<ChangeRequestView?> FindChangeAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> ProposeAsync(Guid id, AccessChange change, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> ApproveAsync(Guid id, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> RejectAsync(Guid id, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> WithdrawAsync(Guid id, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BootstrapAsync(string administrator, string manager, BootstrapEvidence? evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
