namespace IAM.Connectors.Sdk;

/// <summary>
/// Limits on one reconciliation run (DL-082, DL-083). <see cref="MaxMissingFraction"/> and <see cref="MaxMissingCount"/>
/// guard against a mass removal caused by a bad listing rather than a real change: past either, the run is treated as
/// incomplete and nothing is marked missing until people look. A single missing object is always allowed, so small
/// applications aren't blocked. Every value has a hard ceiling, so the guard can't be switched off by configuration.
/// </summary>
public sealed record ReconciliationSettings(int PageSize = 200, int MaxPages = 10_000, double MaxMissingFraction = 0.1, int MaxMissingCount = 500)
{
    public bool IsValid =>
        PageSize is >= 1 and <= ConnectorCapabilities.PageSizeLimit && MaxPages is >= 1 and <= 100_000
        && MaxMissingFraction is >= 0 and <= 0.5 && MaxMissingCount is >= 0 and <= 10_000;
}

/// <summary>Why a reconciliation run can't be trusted to say what is missing. Stored and never renamed.</summary>
public static class ReconciliationCodes
{
    public const string Unhealthy = "Unhealthy";
    public const string ListingFailed = "ListingFailed";
    public const string TooManyPages = "TooManyPages";
    public const string PageTooLarge = "PageTooLarge";
    public const string Duplicate = "DuplicateObject";
    public const string OutOfScope = "OutOfScope";
    public const string SuspiciousDrop = "SuspiciousDrop";
    public const string ConnectorError = "ConnectorError";
}

/// <summary>
/// What a reconciliation run found (DL-082). <see cref="Missing"/> (known to IAM, not found in the target) is filled only
/// when <see cref="Complete"/> is true: an incomplete run never marks anything removed. <see cref="Unexpected"/> (in the
/// target, unknown to IAM) is reported either way, for people to look at. <see cref="ConnectorProductionReady"/> repeats
/// the connector's own claim: nothing may act on a run from a connector that isn't (CLAUDE.md rule 10), and the platform
/// should decide that from its own registry, not this flag alone.
/// </summary>
public sealed record ReconciliationResult(
    bool Complete,
    string? Code,
    IReadOnlyList<ObjectSnapshot> Found,
    IReadOnlyList<ObjectRef> Missing,
    IReadOnlyList<ObjectRef> Unexpected,
    int Pages,
    bool ConnectorProductionReady);

/// <summary>
/// Full discovery of one object type, compared with what IAM expects (spec FR line 1099; DL-082): "a full discovery shall
/// pass connector health, pagination, scope and object-count completeness checks before missing objects are marked
/// removed". Health is checked before and after; the listing must end within <see cref="ReconciliationSettings.MaxPages"/>,
/// no page may exceed the size asked for, no object may appear twice, every object must be the type asked for; and the
/// share of expected objects missing must stay under the mass-removal guard.
/// </summary>
public static class Reconciler
{
    public static async Task<ReconciliationResult> RunAsync(
        IConnector connector,
        string objectType,
        IReadOnlySet<ObjectRef> expected,
        ReconciliationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
        {
            throw new ArgumentException("Reconciliation settings are out of range.", nameof(settings));
        }

        var pageSize = Math.Min(settings.PageSize, connector.Capabilities.MaxPageSize);
        var refs = connector.Capabilities.RefComparer;
        var ready = connector.Capabilities.ProductionReady;
        var known = new HashSet<ObjectRef>(expected.Where(e => string.Equals(e.ObjectType, objectType, StringComparison.Ordinal)), refs);
        var found = new List<ObjectSnapshot>();
        var seen = new HashSet<ObjectRef>(refs);
        var pages = 0;

        ReconciliationResult Incomplete(string code) =>
            new(false, code, found, [], [.. found.Select(f => f.Ref).Where(r => !known.Contains(r))], pages, ready);

        if (!await HealthyAsync(connector, cancellationToken).ConfigureAwait(false))
        {
            return Incomplete(ReconciliationCodes.Unhealthy);
        }

        string? continuation = null;
        do
        {
            if (pages == settings.MaxPages)
            {
                return Incomplete(ReconciliationCodes.TooManyPages);
            }

            ObjectPage page;
            try
            {
                page = await connector.ListAsync(objectType, continuation, pageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return Incomplete(ReconciliationCodes.PageTooLarge);
            }
            catch (ConnectorTransientException)
            {
                return Incomplete(ReconciliationCodes.ListingFailed);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Whatever went wrong, what was seen so far is kept and nothing is marked missing.
                return Incomplete(ReconciliationCodes.ConnectorError);
            }

            pages++;
            if (page.Items.Count > pageSize)
            {
                return Incomplete(ReconciliationCodes.PageTooLarge);
            }

            foreach (var item in page.Items)
            {
                if (!string.Equals(item.Ref.ObjectType, objectType, StringComparison.Ordinal))
                {
                    return Incomplete(ReconciliationCodes.OutOfScope);
                }

                if (!seen.Add(item.Ref))
                {
                    return Incomplete(ReconciliationCodes.Duplicate);
                }

                found.Add(item);
            }

            continuation = page.Continuation;
        }
        while (continuation is not null);

        if (!await HealthyAsync(connector, cancellationToken).ConfigureAwait(false))
        {
            return Incomplete(ReconciliationCodes.Unhealthy);
        }

        var missing = known.Where(e => !seen.Contains(e)).ToList();
        if (missing.Count > settings.MaxMissingCount || missing.Count > Math.Max(1, known.Count * settings.MaxMissingFraction))
        {
            return Incomplete(ReconciliationCodes.SuspiciousDrop);
        }

        return new ReconciliationResult(true, null, found, missing, [.. found.Select(f => f.Ref).Where(r => !known.Contains(r))], pages, ready);
    }

    private static async Task<bool> HealthyAsync(IConnector connector, CancellationToken cancellationToken)
    {
        try
        {
            return (await connector.CheckHealthAsync(cancellationToken).ConfigureAwait(false)).Status == HealthStatus.Healthy;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
