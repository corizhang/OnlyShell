using System.IO;
using System.Text.Json;
using OnlyShell.Core.Scripts;

namespace OnlyShell.App;

public sealed class JsonScriptCenterStore : IScriptCenterStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyShell");
    private static readonly string ScriptsPath = Path.Combine(DirectoryPath, "scripts.json");
    private static readonly string RunsPath = Path.Combine(DirectoryPath, "script-runs.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<ScriptDefinition>> LoadScriptsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<ScriptDefinition>(ScriptsPath, cancellationToken);

    public async Task SaveScriptAsync(ScriptDefinition script, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var scripts = await ReadFileAsync<ScriptDefinition>(ScriptsPath, cancellationToken);
            var index = scripts.FindIndex(item => item.Id == script.Id);
            if (index < 0)
                scripts.Add(script);
            else
                scripts[index] = script;
            await WriteFileAsync(ScriptsPath, scripts
                .OrderBy(item => item.FolderPath, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteScriptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var scripts = await ReadFileAsync<ScriptDefinition>(ScriptsPath, cancellationToken);
            scripts.RemoveAll(item => item.Id == id);
            await WriteFileAsync(ScriptsPath, scripts, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ScriptRunLog>> LoadRecentRunsAsync(int count, CancellationToken cancellationToken = default)
    {
        var runs = await ReadAsync<ScriptRunLog>(RunsPath, cancellationToken);
        return runs.OrderByDescending(item => item.StartedAt).Take(Math.Clamp(count, 1, 500)).ToArray();
    }

    public async Task AppendRunAsync(ScriptRunLog run, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runs = await ReadFileAsync<ScriptRunLog>(RunsPath, cancellationToken);
            runs.Insert(0, run);
            await WriteFileAsync(RunsPath, runs.Take(200).ToArray(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadFileAsync<T>(path, cancellationToken); }
        finally { _gate.Release(); }
    }

    private static async Task<List<T>> ReadFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static async Task WriteFileAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
