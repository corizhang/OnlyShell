using System.IO;
using VirtualTerminal;
using VirtualTerminal.Interfaces;
using VirtualTerminal.Interop;

namespace OnlyShell.Terminal;

public enum LocalShellKind
{
    PowerShell,
    PowerShell7,
    CommandPrompt
}

public interface ILocalTerminalConnector
{
    ITerminalSession Start(LocalShellKind shell, string? workingDirectory = null);
}

public sealed class LocalTerminalConnector : ILocalTerminalConnector
{
    public ITerminalSession Start(LocalShellKind shell, string? workingDirectory = null)
    {
        var application = ResolveApplication(shell);
        var commandLine = shell switch
        {
            LocalShellKind.PowerShell => $"\"{application}\" -NoLogo",
            LocalShellKind.PowerShell7 => $"\"{application}\" -NoLogo",
            LocalShellKind.CommandPrompt => $"\"{application}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(shell))
        };

        return new CommandLineSession(new ProcessCreationInfo
        {
            ApplicationName = application,
            CommandLine = commandLine,
            CurrentDirectory = ResolveWorkingDirectory(workingDirectory)
        });
    }

    private static string ResolveApplication(LocalShellKind shell) => shell switch
    {
        LocalShellKind.PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        LocalShellKind.PowerShell7 => FindOnPath("pwsh.exe")
            ?? throw new FileNotFoundException("未找到 PowerShell 7（pwsh.exe）。请先安装 PowerShell 7，或使用 Windows PowerShell。"),
        LocalShellKind.CommandPrompt => Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(shell))
    };

    private static string ResolveWorkingDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            return path;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : Environment.CurrentDirectory;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
