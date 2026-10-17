namespace OnlyShell.Core.Secrets;

public interface ICredentialStore
{
    Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
