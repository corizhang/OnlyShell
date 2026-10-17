using OnlyShell.Core.Hosts;

namespace OnlyShell.Presentation.Services;

public interface IConnectionCredentialPrompt
{
    Task<ConnectionCredential?> PromptForSecretAsync(HostProfile host, string? savedSecret);
}

public sealed record ConnectionCredential(string Secret, bool RememberOnThisDevice);
