namespace OnlyShell.Core.Hosts;

public sealed record HostProfile(
    Guid Id,
    string Name,
    string HostName,
    int Port,
    string UserName,
    string? Group = null,
    string? CredentialReference = null,
    DateTimeOffset? LastConnectedAt = null,
    bool IsFavorite = false,
    string? Note = null,
    SshAuthenticationMethod AuthenticationMethod = SshAuthenticationMethod.Password,
    string? PrivateKeyPath = null)
{
    public string GroupLabel => string.IsNullOrWhiteSpace(Group) ? "未分组" : Group;
}
