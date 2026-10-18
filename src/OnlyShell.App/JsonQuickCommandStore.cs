using System.IO;
using System.Text.Json;

namespace OnlyShell.App;

public sealed class JsonQuickCommandStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyShell");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "quick-commands.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IReadOnlyList<QuickCommandDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<QuickCommandDefinition>>(
                    await File.ReadAllTextAsync(FilePath, cancellationToken), JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<QuickCommandDefinition> commands, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(commands, JsonOptions), cancellationToken);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
