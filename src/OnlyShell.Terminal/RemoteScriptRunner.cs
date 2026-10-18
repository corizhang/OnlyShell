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
        ScriptExecutionInputs inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(inputs);
        var commandText = BuildCommand(runtime, content, inputs);
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

    private static string BuildCommand(ScriptRuntimeKind runtime, string content, ScriptExecutionInputs inputs)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var environment = string.Join(" ", inputs.EnvironmentVariables.Select(pair => QuoteShellArgument($"{pair.Key}={pair.Value}")));
        var prefix = string.IsNullOrEmpty(environment) ? "" : environment + " ";
        var arguments = string.Join(" ", inputs.Arguments.Select(QuoteShellArgument));
        return runtime switch
        {
            ScriptRuntimeKind.Bash => $"printf '%s' '{encoded}' | base64 -d | env {prefix}bash -s -- {arguments}",
            ScriptRuntimeKind.Python3 => $"env {prefix}python3 -c \"import base64;exec(compile(base64.b64decode('{encoded}'), '<onlyshell>', 'exec'))\" {arguments}",
            _ => throw new ArgumentOutOfRangeException(nameof(runtime))
        };
    }

    private static string QuoteShellArgument(string value) => $"'{value.Replace("'", "'\"'\"'")}'";
}
