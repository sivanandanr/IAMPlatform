using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using Shouldly;

namespace IAM.Connectors.Tests;

/// <summary>
/// Speed limits, faults, drift and reconciliation (DL-082): a throttled or failed operation is retried under the same id
/// and changes things once; a misbehaving directory is reported, never mistaken for removed objects.
/// </summary>
public sealed class ConnectorResilienceTests
{
    private const string Base = MockActiveDirectoryConnector.DefaultBaseDn;
    private static readonly ObjectRef Staff = new(ObjectTypes.Container, $"OU=Staff,{Base}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MockActiveDirectoryConnector Directory(ConnectorSettings? settings = null, TimeProvider? clock = null)
    {
        var directory = new MockActiveDirectoryConnector(settings ?? ConnectorSettings.Default, new InMemoryOperationLedger(), clock: clock);
        directory.AddContainer("Staff");
        directory.AddGroup("Group A", directory.AddContainer("Groups"));
        return directory;
    }

    private static ObjectRef User(string name) => new(ObjectTypes.Account, $"CN={name},OU=Staff,{Base}");

    private static ConnectorOperation Create(ObjectRef user) =>
        new(Guid.CreateVersion7(), ActionCatalog.AccountCreate, user, Staff, new Dictionary<string, string?> { ["displayName"] = user.Id[3..8] }, null);

    private static ConnectorOperation Op(string action, ObjectRef target, IReadOnlyDictionary<string, string?>? attributes = null) =>
        new(Guid.CreateVersion7(), action, target, null, attributes ?? new Dictionary<string, string?>(), null);

    private static async Task<List<ObjectRef>> UsersAsync(MockActiveDirectoryConnector directory, int count)
    {
        var users = Enumerable.Range(0, count).Select(i => User($"user-{i:D3}")).ToList();
        foreach (var user in users)
        {
            (await directory.ExecuteAsync(Create(user), Ct)).Status.ShouldBe(OperationStatus.Applied);
        }

        return users;
    }

    [Fact]
    public async Task TheApplicationsSpeedLimitThrottlesAndAThrottledOperationRunsLater()
    {
        var clock = new ManualClock();
        var directory = Directory(ConnectorSettings.Default with { MaxOperationsPerSecond = 3 }, clock);
        var operations = Enumerable.Range(0, 5).Select(i => Create(User($"burst-{i}"))).ToList();
        var results = new List<OperationResult>();
        foreach (var operation in operations)
        {
            results.Add(await directory.ExecuteAsync(operation, Ct));
        }

        results.Count(r => r.Status == OperationStatus.Applied).ShouldBe(3);
        results.Where(r => r.Status == OperationStatus.Failed).ShouldAllBe(r => r.Code == ResultCodes.Throttled);
        (await directory.ReadAsync(operations[4].Target, Ct)).ShouldBeNull("a throttled operation changes nothing");

        // A repeat of a finished operation is answered from the ledger and costs nothing, even with no capacity left.
        (await directory.ExecuteAsync(operations[0], Ct)).Status.ShouldBe(OperationStatus.Applied);

        clock.Advance(TimeSpan.FromSeconds(1));
        (await directory.ExecuteAsync(operations[4], Ct)).Status.ShouldBe(OperationStatus.Applied, "retried under the same id once there is capacity");
    }

    [Fact]
    public async Task TheLowerOfTheConnectorsAndTheApplicationsLimitsApplies()
    {
        var clock = new ManualClock();
        var generous = Directory(ConnectorSettings.Default with { MaxOperationsPerSecond = 10_000 }, clock);
        var applied = 0;
        for (var i = 0; i < generous.Capabilities.MaxOperationsPerSecond + 5; i++)
        {
            applied += (await generous.ExecuteAsync(Create(User($"g-{i}")), Ct)).Status == OperationStatus.Applied ? 1 : 0;
        }

        applied.ShouldBe(generous.Capabilities.MaxOperationsPerSecond, "the connector's declared limit caps a generous application");
    }

    [Fact]
    public void TheBucketRefillsSteadilyAndNeverBanksMoreThanASecond()
    {
        var clock = new ManualClock();
        var limiter = new OperationRateLimiter(4, clock);
        Enumerable.Range(0, 4).All(_ => limiter.TryAcquire()).ShouldBeTrue();
        limiter.TryAcquire().ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        limiter.TryAcquire().ShouldBeTrue();
        limiter.TryAcquire().ShouldBeFalse();
        clock.Advance(TimeSpan.FromHours(1));
        Enumerable.Range(0, 5).Count(_ => limiter.TryAcquire()).ShouldBe(4);
        Should.Throw<ArgumentOutOfRangeException>(() => new OperationRateLimiter(0, clock));
    }

    [Fact]
    public async Task TransientWriteFailuresAreRetriedUnderTheSameIdAndApplyOnce()
    {
        var directory = Directory();
        var user = (await UsersAsync(directory, 1))[0];
        directory.Faults.FailureCode = ResultCodes.TimedOut;
        directory.Faults.FailNextWrites(2);
        var enable = Op(ActionCatalog.AccountEnable, user);
        (await directory.ExecuteAsync(enable, Ct)).ShouldBe(new OperationResult(enable.OperationId, OperationStatus.Failed, ResultCodes.TimedOut));
        (await directory.ExecuteAsync(enable, Ct)).Code.ShouldBe(ResultCodes.TimedOut);
        (await directory.ExecuteAsync(enable, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(user, Ct)).ShouldNotBeNull().Enabled.ShouldBe(true);
    }

    [Fact]
    public async Task AnUnavailableDirectorySaysSoEverywhere()
    {
        var directory = Directory();
        var user = (await UsersAsync(directory, 1))[0];
        directory.Faults.Unavailable = true;

        (await directory.CheckHealthAsync(Ct)).Status.ShouldBe(HealthStatus.Unavailable);
        var enable = Op(ActionCatalog.AccountEnable, user);
        (await directory.ExecuteAsync(enable, Ct)).ShouldBe(new OperationResult(enable.OperationId, OperationStatus.Failed, ResultCodes.TargetUnavailable));
        (await directory.VerifyAsync(enable, Ct)).ShouldBe(new VerificationResult(false, ResultCodes.TargetUnavailable));
        await Should.ThrowAsync<ConnectorTransientException>(() => directory.ReadAsync(user, Ct));
        await Should.ThrowAsync<ConnectorTransientException>(() => directory.ListAsync(ObjectTypes.Account, null, 10, Ct));

        directory.Faults.Unavailable = false;
        (await directory.ExecuteAsync(enable, Ct)).Status.ShouldBe(OperationStatus.Applied);
    }

    [Fact]
    public async Task LatencyDelaysEveryCall()
    {
        var clock = new ManualClock();
        var directory = Directory(clock: clock);
        directory.Faults.Latency = TimeSpan.FromSeconds(5);
        var read = directory.ReadAsync(Staff, Ct);
        await Task.Delay(50, Ct);
        read.IsCompleted.ShouldBeFalse("still waiting on the connector's clock");
        clock.Advance(TimeSpan.FromSeconds(5));
        (await read).ShouldNotBeNull();
    }

    [Fact]
    public async Task DriftIsSeenByVerificationAndPutRightByAnUpdate()
    {
        var directory = Directory();
        var user = (await UsersAsync(directory, 1))[0];
        var update = Op(ActionCatalog.AccountUpdate, user, new Dictionary<string, string?> { ["department"] = "Finance" });
        (await directory.ExecuteAsync(update, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.VerifyAsync(update, Ct)).Matches.ShouldBeTrue();

        directory.DriftAttribute(user, "department", "Sales");
        (await directory.VerifyAsync(update, Ct)).Matches.ShouldBeFalse("someone changed it outside IAM");
        (await directory.ExecuteAsync(update with { OperationId = Guid.CreateVersion7() }, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.VerifyAsync(update, Ct)).Matches.ShouldBeTrue();

        var group = new ObjectRef(ObjectTypes.Entitlement, $"CN=Group A,OU=Groups,{Base}");
        directory.DriftAddMember(group, user);
        (await directory.ReadAsync(user, Ct)).ShouldNotBeNull().Links.ShouldBe([group]);
        directory.DriftRemove(user);
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountEnable, user), Ct)).Code.ShouldBe(ResultCodes.TargetNotFound);
        (await directory.ReadAsync(group, Ct)).ShouldNotBeNull().Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACompleteRunReportsMissingAndUnexpectedObjects()
    {
        var directory = Directory();
        var users = await UsersAsync(directory, 12);
        var ghost = User("left-last-year");
        var expected = users.Skip(1).Append(ghost).ToHashSet();
        var result = await Reconciler.RunAsync(directory, ObjectTypes.Account, expected, new ReconciliationSettings(PageSize: 5), Ct);

        result.Complete.ShouldBeTrue();
        result.Code.ShouldBeNull();
        result.Pages.ShouldBe(3);
        result.Found.Count.ShouldBe(12);
        result.Missing.ShouldBe([ghost]);
        result.Unexpected.ShouldBe([users[0]]);
    }

    [Fact]
    public async Task AnUnhealthyOrFailingRunMarksNothingMissing()
    {
        var down = Directory();
        var users = await UsersAsync(down, 6);
        var expected = users.Append(User("ghost")).ToHashSet();
        down.Faults.Unavailable = true;
        var unhealthy = await Reconciler.RunAsync(down, ObjectTypes.Account, expected, new ReconciliationSettings(PageSize: 2), Ct);
        unhealthy.ShouldSatisfyAllConditions(
            () => unhealthy.Complete.ShouldBeFalse(),
            () => unhealthy.Code.ShouldBe(ReconciliationCodes.Unhealthy),
            () => unhealthy.Missing.ShouldBeEmpty());

        down.Faults.Unavailable = false;
        down.Faults.FailListingAfterPages = 1;
        var partial = await Reconciler.RunAsync(down, ObjectTypes.Account, expected, new ReconciliationSettings(PageSize: 2), Ct);
        partial.Complete.ShouldBeFalse();
        partial.Code.ShouldBe(ReconciliationCodes.ListingFailed);
        partial.Found.Count.ShouldBe(2, "what was seen is still reported");
        partial.Missing.ShouldBeEmpty("an incomplete listing never marks anything removed");
    }

    [Fact]
    public async Task AMassDisappearanceIsTreatedAsABadListing()
    {
        var directory = Directory();
        var users = await UsersAsync(directory, 3);
        var expected = users.Concat(Enumerable.Range(0, 10).Select(i => User($"ghost-{i}"))).ToHashSet();
        var result = await Reconciler.RunAsync(directory, ObjectTypes.Account, expected, new ReconciliationSettings(), Ct);
        result.Complete.ShouldBeFalse();
        result.Code.ShouldBe(ReconciliationCodes.SuspiciousDrop);
        result.Missing.ShouldBeEmpty();

        var capped = await Reconciler.RunAsync(directory, ObjectTypes.Account, users.Append(User("ghost")).ToHashSet(), new ReconciliationSettings(MaxMissingFraction: 1, MaxMissingCount: 0), Ct);
        capped.Code.ShouldBe(ReconciliationCodes.SuspiciousDrop, "the count limit holds on its own");
    }

    [Fact]
    public async Task AListingThatNeverEndsIsIncomplete()
    {
        var directory = Directory();
        await UsersAsync(directory, 5);
        var result = await Reconciler.RunAsync(directory, ObjectTypes.Account, new HashSet<ObjectRef>(), new ReconciliationSettings(PageSize: 2, MaxPages: 2), Ct);
        result.Complete.ShouldBeFalse();
        result.Code.ShouldBe(ReconciliationCodes.TooManyPages);
        await Should.ThrowAsync<ArgumentException>(() => Reconciler.RunAsync(directory, ObjectTypes.Account, new HashSet<ObjectRef>(), new ReconciliationSettings(MaxMissingFraction: 2), Ct));
    }

    [Theory]
    [InlineData(BadListing.Kind.Duplicate, ReconciliationCodes.Duplicate)]
    [InlineData(BadListing.Kind.WrongType, ReconciliationCodes.OutOfScope)]
    [InlineData(BadListing.Kind.Oversized, ReconciliationCodes.PageTooLarge)]
    public async Task AListingThatBreaksTheRulesIsIncomplete(BadListing.Kind kind, string code)
    {
        var expected = new HashSet<ObjectRef> { new(ObjectTypes.Account, "kept"), new(ObjectTypes.Account, "gone") };
        var result = await Reconciler.RunAsync(new BadListing(kind), ObjectTypes.Account, expected, new ReconciliationSettings(PageSize: 2), Ct);
        result.Complete.ShouldBeFalse();
        result.Code.ShouldBe(code);
        result.Missing.ShouldBeEmpty();
    }

    /// <summary>A connector whose listing breaks one rule: a repeated object, the wrong type, or an oversized page.</summary>
    public sealed class BadListing(BadListing.Kind kind) : IConnector
    {
        public enum Kind
        {
            Duplicate = 1,
            WrongType = 2,
            Oversized = 3,
        }

        public ConnectorCapabilities Capabilities { get; } = new(
            "test.bad_listing", false, [ActionCatalog.AccountEnable], [new ObjectTypeCapability(ObjectTypes.Account, true, ["displayName"])], 10, 10);

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, null, false));

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => Task.FromResult<ObjectSnapshot?>(null);

        public Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken)
        {
            static ObjectSnapshot Item(string type, string id) => new(new ObjectRef(type, id), true, false, new Dictionary<string, string?>(), [], null);
            ObjectPage page = kind switch
            {
                Kind.Duplicate => continuation is null ? new([Item(ObjectTypes.Account, "kept")], "2") : new([Item(ObjectTypes.Account, "kept")], null),
                Kind.WrongType => new([Item(ObjectTypes.Account, "kept"), Item(ObjectTypes.Entitlement, "group")], null),
                _ => new([Item(ObjectTypes.Account, "kept"), Item(ObjectTypes.Account, "a"), Item(ObjectTypes.Account, "b")], null),
            };
            return Task.FromResult(page);
        }
    }

    /// <summary>A clock tests move by hand. Delays wait until it is moved past them.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<(long Due, ITimerCallbackHandle Timer)> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _ticks;
            }
        }

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_sync)
            {
                _timers.Add((_ticks + dueTime.Ticks, timer));
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ITimerCallbackHandle> due;
            lock (_sync)
            {
                _ticks += by.Ticks;
                due = [.. _timers.Where(t => t.Due <= _ticks).Select(t => t.Timer)];
                _timers.RemoveAll(t => t.Due <= _ticks);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private interface ITimerCallbackHandle
        {
            void Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer, ITimerCallbackHandle
        {
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
