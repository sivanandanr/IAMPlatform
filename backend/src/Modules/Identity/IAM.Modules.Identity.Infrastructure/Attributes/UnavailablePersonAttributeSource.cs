using IAM.Modules.Identity.Contracts;

namespace IAM.Modules.Identity.Infrastructure.Attributes;

/// <summary>
/// Phase 0 has no HR source (DL-090g): every person has no attribute values. Matrix conditions on people hold and naming rules
/// fail closed. Replaced by the HR sync stage; not production-ready (CLAUDE.md rule 10).
/// </summary>
public sealed class UnavailablePersonAttributeSource : IPersonAttributeSource
{
    public Task<IReadOnlyList<PersonAttributeValue>> FindAsync(string person, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PersonAttributeValue>>([]);
}
