namespace OnlyShell.Core.Hosts;

public interface IHostRepository
{
    Task<IReadOnlyList<HostProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(HostProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
