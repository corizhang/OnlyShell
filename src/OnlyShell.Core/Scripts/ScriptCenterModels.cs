using OnlyShell.Core.Hosts;

namespace OnlyShell.Core.Scripts;

public enum ScriptRuntimeKind
{
    Bash,
    Python3,
    PowerShell,
    Cmd
}

public sealed record ScriptDefinition(
    Guid Id,
    string Name,
    ScriptRuntimeKind Runtime,
    string Description,
    string Content,
    DateTimeOffset UpdatedAt,
    string FolderPath = "",
    string ExecutionTarget = "Remote");

public sealed record ScriptRunLog(
    Guid Id,
    Guid ScriptId,
    string ScriptName,
    Guid? HostId,
    string HostName,
    DateTimeOffset StartedAt,
    long DurationMilliseconds,
    int? ExitCode,
    string Output,
    string Error,
    bool Succeeded,
    string TargetType = "Remote");

public sealed record RemoteScriptRunResult(int ExitCode, string Output, string Error);

public interface IScriptCenterStore
{
    Task<IReadOnlyList<ScriptDefinition>> LoadScriptsAsync(CancellationToken cancellationToken = default);
    Task SaveScriptAsync(ScriptDefinition script, CancellationToken cancellationToken = default);
    Task DeleteScriptAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScriptRunLog>> LoadRecentRunsAsync(int count, CancellationToken cancellationToken = default);
    Task AppendRunAsync(ScriptRunLog run, CancellationToken cancellationToken = default);
}

public interface IRemoteScriptRunner
{
    Task<RemoteScriptRunResult> RunAsync(
        HostProfile host,
        string secret,
        ScriptRuntimeKind runtime,
        string content,
        CancellationToken cancellationToken = default);
}

public interface ILocalScriptRunner
{
    Task<RemoteScriptRunResult> RunAsync(
        ScriptRuntimeKind runtime,
        string content,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        Action<string, bool>? outputReceived = null);
}

public interface IScriptCenterInteractionService
{
    Task<bool> ConfirmDeleteAsync(ScriptDefinition script);
    Task<bool> ConfirmLocalExecutionAsync();
}
