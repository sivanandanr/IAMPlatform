namespace IAM.Modules.Provisioning.Domain.Items;

/// <summary>
/// What a connector remembers about one finished operation (DL-080, DL-093): what was asked (as the connector's fingerprint)
/// and how it ended. Durable, so a restarted connector still recognises a repeat (R-039). The first record of an operation
/// wins and is never changed or deleted.
/// </summary>
public sealed class OperationLedgerRecord
{
    public const int FingerprintLength = 64;
    public const int StatusMaxLength = 32;
    public const int CodeMaxLength = 64;

    private OperationLedgerRecord()
    {
        Fingerprint = Status = string.Empty;
    }

    public Guid OperationId { get; private set; }

    public string Fingerprint { get; private set; }

    public string Status { get; private set; }

    public string? Code { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static OperationLedgerRecord Create(Guid operationId, string fingerprint, string status, string? code, DateTimeOffset now)
    {
        if (operationId == Guid.Empty
            || fingerprint is not { Length: FingerprintLength } || !fingerprint.All(char.IsAsciiHexDigitLower)
            || status is not { Length: > 0 and <= StatusMaxLength } || !status.All(char.IsAsciiLetter)
            || (code is not null && (code.Length is 0 or > CodeMaxLength || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))))
        {
            throw new ArgumentException("A ledger record needs an operation id, a lower-case SHA-256 fingerprint, a status word and an optional code.", nameof(operationId));
        }

        return new OperationLedgerRecord { OperationId = operationId, Fingerprint = fingerprint, Status = status, Code = code, RecordedAtUtc = now };
    }
}
