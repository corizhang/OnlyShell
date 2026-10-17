namespace OnlyShell.Core.Security;

public interface IKnownHostRepository
{
    string? FindFingerprint(string hostName, int port);
    void TrustFingerprint(string hostName, int port, string fingerprint);
}

public interface IHostKeyConfirmationService
{
    bool Confirm(OnlyShell.Core.Hosts.HostProfile host, string fingerprint, string? previouslyTrustedFingerprint);
}
