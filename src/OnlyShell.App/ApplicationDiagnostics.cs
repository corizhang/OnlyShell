using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace OnlyShell.App;

internal static class ApplicationDiagnostics
{
    private static readonly object Gate = new();
    private const long MaxLogBytes = 4 * 1024 * 1024;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnlyShell", "logs");

    public static string CurrentLogPath => Path.Combine(DirectoryPath, "onlyshell.log");

    public static void Write(string category, string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                var entry = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] [")
                    .Append(category).Append("] ").AppendLine(message);
                if (exception is not null)
                    entry.AppendLine(exception.ToString());
                File.AppendAllText(CurrentLogPath, entry.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never prevent the application from starting or shutting down.
        }
    }

    public static void Export(string destinationPath)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var reportPath = Path.Combine(DirectoryPath, "diagnostic-report.txt");
            var assembly = Assembly.GetEntryAssembly();
            var report = $"OnlyShell diagnostic report\r\n" +
                         $"Created: {DateTimeOffset.Now:O}\r\n" +
                         $"Version: {assembly?.GetName().Version?.ToString() ?? "unknown"}\r\n" +
                         $"Runtime: {Environment.Version}\r\n" +
                         $"OS: {Environment.OSVersion}\r\n" +
                         $"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\r\n" +
                         $"Log directory: {DirectoryPath}\r\n";
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));

            using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(reportPath, "diagnostic-report.txt");
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "onlyshell*.log"))
                archive.CreateEntryFromFile(path, Path.GetFileName(path));
        }
    }

    private static void RotateIfNeeded()
    {
        var current = new FileInfo(CurrentLogPath);
        if (!current.Exists || current.Length < MaxLogBytes)
            return;

        var previous = Path.Combine(DirectoryPath, "onlyshell.previous.log");
        if (File.Exists(previous))
            File.Delete(previous);
        File.Move(CurrentLogPath, previous);
    }
}
