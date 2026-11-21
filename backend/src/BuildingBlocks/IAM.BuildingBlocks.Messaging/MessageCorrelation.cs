using System.Diagnostics;

namespace IAM.BuildingBlocks.Messaging;

/// <summary>
/// One correlation id from a request's submission to its target (DL-094): a W3C trace id (32 lower-case hex digits, not all
/// zero). A consumer handles a message under a trace whose id is the message's correlation id, so what it audits and the
/// messages it writes carry the same id; work that runs later from stored state uses the id stored with that state.
/// </summary>
public static class MessageCorrelation
{
    public const int Length = 32;

    /// <summary>Whether <paramref name="value"/> is a trace id this platform carries.</summary>
    public static bool IsTraceId(string? value) =>
        value is { Length: Length } && value.All(char.IsAsciiHexDigitLower) && value.Any(c => c != '0');

    /// <summary>The current trace's id, or a new one when there is no trace.</summary>
    public static string Current() => Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();

    /// <summary>
    /// Starts a trace with the envelope's correlation id for the handling of one message (dispose it to end it), or returns null
    /// when the id isn't a trace id, so the handling keeps its own.
    /// </summary>
    public static Activity? StartHandling(MessageEnvelope envelope, string consumer)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return StartFor(envelope.CorrelationId, $"iam.consume {consumer}");
    }

    /// <summary>Starts a trace with a stored correlation id (work that runs later from stored state), or null when there is none.</summary>
    public static Activity? StartFor(string? correlationId, string operation)
    {
        if (!IsTraceId(correlationId))
        {
            return null;
        }

        var activity = new Activity(operation);
        activity.SetParentId(ActivityTraceId.CreateFromString(correlationId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        return activity.Start();
    }
}
