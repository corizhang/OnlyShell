using System.Text;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Security;
using OnlyShell.Core.Scripts;
using OnlyShell.Infrastructure.Ssh;
using Renci.SshNet;

namespace OnlyShell.Terminal;

public sealed class RemoteScriptRunner(
    IKnownHostRepository knownHosts,
    IHostKeyConfirmationService confirmation) : IRemoteScriptRunner
{
    public async Task<RemoteScriptRunResult> RunAsync(
        HostProfile host,
        string secret,
        ScriptRuntimeKind runtime,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var commandText = BuildCommand(runtime, content);
        using var client = new SshClient(SshConnectionInfoFactory.Create(host, secret));
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        client.HostKeyReceived += (_, key) =>
        {
            var fingerprint = $"SHA256:{key.FingerPrintSHA256}";
            var previous = knownHosts.FindFingerprint(host.HostName, host.Port);
            key.CanTrust = string.Equals(previous, fingerprint, StringComparison.Ordinal)
                || confirmation.Confirm(host, fingerprint, previous);
            if (key.CanTrust && !string.Equals(previous, fingerprint, StringComparison.Ordinal))
                knownHosts.TrustFingerprint(host.HostName, host.Port, fingerprint);
        };

        await client.ConnectAsync(cancellationToken);
        try
        {
            using var command = client.CreateCommand(commandText);
            await command.ExecuteAsync(cancellationToken);
            return new RemoteScriptRunResult(command.ExitStatus ?? -1, command.Result, command.Error ?? string.Empty);
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    private static string BuildCommand(ScriptRuntimeKind runtime, string content)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        return runtime switch
        {
            ScriptRuntimeKind.Bash => $"printf '%s' '{encoded}' | base64 -d | bash -s",
            ScriptRuntimeKind.Python3 => $"python3 -c \"import base64;exec(compile(base64.b64decode('{encoded}'), '<onlyshell>', 'exec'))\"",
            _ => throw new ArgumentOutOfRangeException(nameof(runtime))
        };
    }
}
