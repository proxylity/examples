namespace DnsFilterLambda;

public interface IDomainStateStore
{
    Task<DomainState?> GetAsync(string domain, CancellationToken cancellationToken = default);
    Task<DomainState?[]> BatchGetAsync(ICollection<string> domains, CancellationToken cancellationToken = default);
    Task UpdateAsync(DomainState updatedState, CancellationToken cancellationToken = default);
}
