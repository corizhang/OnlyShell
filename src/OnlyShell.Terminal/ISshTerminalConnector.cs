using OnlyShell.Core.Hosts;
using VirtualTerminal;

namespace OnlyShell.Terminal;

public interface ISshTerminalConnector
{
    Task<SecureShellSession> ConnectAsync(HostProfile host, string secret, CancellationToken cancellationToken = default);
}

