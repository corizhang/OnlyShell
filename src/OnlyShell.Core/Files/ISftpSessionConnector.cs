using OnlyShell.Core.Hosts;

namespace OnlyShell.Core.Files;

public interface ISftpSessionConnector
{
    Task<ISftpSession> ConnectAsync(HostProfile host, string secret, CancellationToken cancellationToken = default);
}
