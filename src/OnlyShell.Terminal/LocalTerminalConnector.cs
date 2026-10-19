using System.IO;
using System.Text;
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
            LocalShellKind.PowerShell => BuildPowerShellCommandLine(application),
            LocalShellKind.PowerShell7 => BuildPowerShellCommandLine(application),
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

    private static string BuildPowerShellCommandLine(string application)
    {
        // OSC title updates provide a side channel for current-directory Git context without
        // writing status commands into the visible terminal or modifying the user's profile.
        const string integration = "$global:OnlyShellOriginalPrompt = $function:prompt; function global:prompt { " +
            "$location = (Get-Location).Path; $branch = ''; $dirty = '0'; " +
            "if (Get-Command git -ErrorAction SilentlyContinue) { " +
            "$root = git -C $location rev-parse --show-toplevel 2>$null; " +
            "if ($LASTEXITCODE -eq 0) { $branch = git -C $location branch --show-current 2>$null; " +
            "if (-not $branch) { $branch = 'detached@' + (git -C $location rev-parse --short HEAD 2>$null) }; " +
            "$changes = git -C $location status --porcelain 2>$null; if ($changes) { $dirty = '1' } } }; " +
            "$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($location)); " +
            "[Console]::Write([char]27 + ']0;OnlyShellGit|' + $encoded + '|' + $branch + '|' + $dirty + [char]7); " +
            "if ($global:OnlyShellOriginalPrompt) { & $global:OnlyShellOriginalPrompt } else { 'PS ' + $location + '> ' } }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(integration));
        return $"\"{application}\" -NoLogo -NoExit -EncodedCommand {encodedCommand}";
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
