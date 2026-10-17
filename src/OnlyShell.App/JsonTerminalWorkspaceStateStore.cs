using System.IO;
using System.Text.Json;
using OnlyShell.Presentation.Services;

namespace OnlyShell.App;

public sealed class JsonTerminalWorkspaceStateStore : ITerminalWorkspaceStateStore
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyShell", "terminal-workspace.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TerminalWorkspaceState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StatePath))
                return null;
            var state = JsonSerializer.Deserialize<TerminalWorkspaceState>(await File.ReadAllTextAsync(StatePath, cancellationToken), JsonOptions);
            if (state?.SchemaVersion != 1 || state.Tabs is null)
                return null;
            return state;
        }
        catch (JsonException) { return null; }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TerminalWorkspaceState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = StatePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}
