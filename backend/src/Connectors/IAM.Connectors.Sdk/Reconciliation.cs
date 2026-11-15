namespace IAM.Connectors.Sdk;

/// <summary>
/// Limits on one reconciliation run (DL-082). <see cref="MaxMissingFraction"/> and <see cref="MaxMissingCount"/> guard
/// against a mass removal caused by a bad listing rather than a real change: past either, the run is treated as
/// incomplete and nothing is marked missing until people look.
/// </summary>
public sealed record ReconciliationSettings(int PageSize = 200, int MaxPages = 10_000, double MaxMissingFraction = 0.1, int MaxMissingCount = 500)
{
    public bool IsValid => PageSize >= 1 && MaxPages >= 1 && MaxMissingFraction is >= 0 and <= 1 && MaxMissingCount >= 0;
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
}

/// <summary>
/// What a reconciliation run found (DL-082). <see cref="Missing"/> (known to IAM, not found in the target) is filled only
/// when <see cref="Complete"/> is true: an incomplete run never marks anything removed. <see cref="Unexpected"/> (in the
/// target, unknown to IAM) is reported either way, for people to look at.
/// </summary>
public sealed record ReconciliationResult(
    bool Complete,
    string? Code,
    IReadOnlyList<ObjectSnapshot> Found,
    IReadOnlyList<ObjectRef> Missing,
    IReadOnlyList<ObjectRef> Unexpected,
    int Pages);

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
        var found = new List<ObjectSnapshot>();
        var seen = new HashSet<ObjectRef>();
        var pages = 0;

        ReconciliationResult Incomplete(string code) =>
            new(false, code, found, [], [.. found.Select(f => f.Ref).Where(r => !expected.Contains(r))], pages);

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
            catch (ConnectorTransientException)
            {
                return Incomplete(ReconciliationCodes.ListingFailed);
            }
            catch (InvalidOperationException)
            {
                return Incomplete(ReconciliationCodes.PageTooLarge);
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

        var missing = expected.Where(e => string.Equals(e.ObjectType, objectType, StringComparison.Ordinal) && !seen.Contains(e)).ToList();
        var known = expected.Count(e => string.Equals(e.ObjectType, objectType, StringComparison.Ordinal));
        if (missing.Count > settings.MaxMissingCount || (known > 0 && missing.Count > known * settings.MaxMissingFraction))
        {
            return Incomplete(ReconciliationCodes.SuspiciousDrop);
        }

        return new ReconciliationResult(true, null, found, missing, [.. found.Select(f => f.Ref).Where(r => !expected.Contains(r))], pages);
    }

    private static async Task<bool> HealthyAsync(IConnector connector, CancellationToken cancellationToken)
    {
        try
        {
            return (await connector.CheckHealthAsync(cancellationToken).ConfigureAwait(false)).Status == HealthStatus.Healthy;
        }
        catch (ConnectorTransientException)
        {
            return false;
        }
    }
}
