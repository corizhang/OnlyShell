using OnlyShell.Core.Hosts;
using Renci.SshNet;

namespace OnlyShell.Infrastructure.Ssh;

public static class SshConnectionInfoFactory
{
    public static ConnectionInfo Create(HostProfile host, string? secret)
    {
        AuthenticationMethod authenticationMethod;
        if (host.AuthenticationMethod == SshAuthenticationMethod.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(host.PrivateKeyPath) || !File.Exists(host.PrivateKeyPath))
                throw new FileNotFoundException("找不到主机配置中的 SSH 私钥文件。", host.PrivateKeyPath);

            var keyFile = new PrivateKeyFile(host.PrivateKeyPath, secret ?? string.Empty);
            authenticationMethod = new PrivateKeyAuthenticationMethod(host.UserName, keyFile);
        }
        else
        {
            authenticationMethod = new PasswordAuthenticationMethod(host.UserName, secret ?? string.Empty);
        }

        var connectionInfo = new ConnectionInfo(host.HostName, host.Port, host.UserName, authenticationMethod)
        {
            Timeout = TimeSpan.FromSeconds(15),
            RetryAttempts = 2
        };
        return connectionInfo;
    }
}
