using OnlyShell.Core.Hosts;

namespace OnlyShell.Core.Scripts;

public enum ScriptRuntimeKind
{
    Bash,
    Python3,
    PowerShell,
    Cmd
}

public enum ScriptInputValueType
{
    Text,
    Number,
    Boolean,
    Path,
    Choice,
    Secret
}

public sealed record ScriptInputFieldDefinition(
    string Kind,
    string Name,
    ScriptInputValueType ValueType = ScriptInputValueType.Text,
    bool IsRequired = false,
    bool IsSensitive = false,
    string Options = "",
    string Group = "");

public sealed record ScriptDefinition(
    Guid Id,
    string Name,
    ScriptRuntimeKind Runtime,
    string Description,
    string Content,
    DateTimeOffset UpdatedAt,
    string FolderPath = "",
    string ExecutionTarget = "Remote",
    string InputTemplate = "",
    IReadOnlyList<ScriptInputFieldDefinition>? InputFields = null);

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

public sealed record ScriptExecutionInputs(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

public interface IScriptCenterStore
{
    Task<IReadOnlyList<ScriptDefinition>> LoadScriptsAsync(CancellationToken cancellationToken = default);
    Task SaveScriptAsync(ScriptDefinition script, CancellationToken cancellationToken = default);
    Task DeleteScriptAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScriptRunLog>> LoadRecentRunsAsync(int count, CancellationToken cancellationToken = default);
    Task AppendRunAsync(ScriptRunLog run, CancellationToken cancellationToken = default);
}

public interface IScriptExecutionInputsStore
{
    Task<ScriptExecutionInputs?> LoadAsync(Guid scriptId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid scriptId, ScriptExecutionInputs inputs, CancellationToken cancellationToken = default);
    Task<ScriptExecutionInputs?> LoadDefaultsAsync(Guid scriptId, CancellationToken cancellationToken = default);
    Task SaveDefaultsAsync(Guid scriptId, ScriptExecutionInputs inputs, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid scriptId, CancellationToken cancellationToken = default);
}

public interface IRemoteScriptRunner
{
    Task<RemoteScriptRunResult> RunAsync(
        HostProfile host,
        string secret,
        ScriptRuntimeKind runtime,
        string content,
        ScriptExecutionInputs inputs,
        CancellationToken cancellationToken = default);
}

public interface ILocalScriptRunner
{
    Task<RemoteScriptRunResult> RunAsync(
        ScriptRuntimeKind runtime,
        string content,
        string workingDirectory,
        ScriptExecutionInputs inputs,
        CancellationToken cancellationToken = default,
        Action<string, bool>? outputReceived = null);
}

public interface IScriptCenterInteractionService
{
    Task<bool> ConfirmDeleteAsync(ScriptDefinition script);
    Task<bool> ConfirmLocalExecutionAsync();
    Task<IReadOnlyList<HostProfile>?> SelectHostsForBatchAsync(IReadOnlyList<HostProfile> hosts, Guid? initiallySelectedHostId);
    Task<ScriptExecutionInputs?> PromptForExecutionInputsAsync(
        ScriptExecutionInputs initialInputs,
        IReadOnlyList<ScriptInputFieldDefinition> fields);
}
