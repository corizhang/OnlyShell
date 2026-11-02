namespace OnlyShell.Core.Secrets;

public interface ICredentialStore
{
    Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>Credential Manager namespace reserved for AI provider credentials.</summary>
public interface IAiCredentialStore : ICredentialStore
{
}
