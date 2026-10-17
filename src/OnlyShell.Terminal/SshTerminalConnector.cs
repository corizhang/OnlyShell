using OnlyShell.Core.Hosts;
using OnlyShell.Core.Security;
using OnlyShell.Infrastructure.Ssh;
using Renci.SshNet;
using VirtualTerminal;

namespace OnlyShell.Terminal;

public sealed class SshTerminalConnector(
    IKnownHostRepository knownHosts,
    IHostKeyConfirmationService confirmation) : ISshTerminalConnector
{
    public async Task<SecureShellSession> ConnectAsync(
        HostProfile host,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var session = new SecureShellSession(SshConnectionInfoFactory.Create(host, secret));
        session.Client!.HostKeyReceived += (_, key) =>
        {
            var fingerprint = $"SHA256:{key.FingerPrintSHA256}";
            var previous = knownHosts.FindFingerprint(host.HostName, host.Port);

            if (string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                key.CanTrust = true;
                return;
            }

            key.CanTrust = confirmation.Confirm(host, fingerprint, previous);
            if (key.CanTrust)
                knownHosts.TrustFingerprint(host.HostName, host.Port, fingerprint);
        };

        try
        {
            await session.ConnectAsync(cancellationToken);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
