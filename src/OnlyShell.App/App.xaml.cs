using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Files;
using OnlyShell.Core.Security;
using OnlyShell.Core.Secrets;
using OnlyShell.Core.Scripts;
using OnlyShell.Infrastructure.Data;
using OnlyShell.Infrastructure.Secrets;
using OnlyShell.Infrastructure.Ssh;
using OnlyShell.Presentation.ViewModels;
using OnlyShell.Presentation.Services;
using OnlyShell.Terminal;
using Velopack;
using Velopack.Sources;
using System.Reflection;

namespace OnlyShell.App;

public partial class App : Application
{
    private IHost? _applicationHost;

    public static string ProductVersion
    {
        get
        {
            var version = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
                version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            var metadataSeparator = version.IndexOf('+');
            return $"v{(metadataSeparator >= 0 ? version[..metadataSeparator] : version)}";
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        ApplicationDiagnostics.Write("APP", $"Starting OnlyShell {typeof(App).Assembly.GetName().Version}; args={e.Args.Length}");

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<SqliteHostRepository>(_ => new SqliteHostRepository(GetDatabasePath()));
            builder.Services.AddSingleton<IHostRepository>(provider => provider.GetRequiredService<SqliteHostRepository>());
            builder.Services.AddSingleton<IKnownHostRepository>(_ => new SqliteKnownHostRepository(GetDatabasePath()));
            builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
            builder.Services.AddSingleton<IScriptExecutionInputsStore, WindowsProtectedScriptInputsStore>();
            builder.Services.AddSingleton<ISshTerminalConnector, SshTerminalConnector>();
            builder.Services.AddSingleton<ILocalTerminalConnector, LocalTerminalConnector>();
            builder.Services.AddSingleton<IRemoteScriptRunner, RemoteScriptRunner>();
            builder.Services.AddSingleton<ILocalScriptRunner, LocalScriptRunner>();
            builder.Services.AddSingleton<ISftpSessionConnector, SftpSessionConnector>();
            builder.Services.AddSingleton<IHostKeyConfirmationService, WpfHostKeyConfirmationService>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.IConnectionCredentialPrompt, WpfConnectionCredentialPrompt>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.IHostEditorDialogService, WpfHostEditorDialogService>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.ISftpDialogService, WpfSftpDialogService>();
            builder.Services.AddSingleton<HttpClient>();
            builder.Services.AddSingleton<ISyncInteractionService, WpfSyncInteractionService>();
            builder.Services.AddSingleton<IGitHubSyncService, GitHubSyncService>();
            builder.Services.AddSingleton<ITerminalWorkspaceStateStore, JsonTerminalWorkspaceStateStore>();
            builder.Services.AddSingleton<IScriptCenterStore, JsonScriptCenterStore>();
            builder.Services.AddSingleton<IScriptCenterInteractionService, WpfScriptCenterInteractionService>();
            builder.Services.AddSingleton<WorkspaceViewModel>();
            builder.Services.AddTransient<MainWindow>();
            _applicationHost = builder.Build();

            await _applicationHost.Services.GetRequiredService<SqliteHostRepository>().InitializeAsync();
            await _applicationHost.Services.GetRequiredService<WorkspaceViewModel>().LoadAsync();
            await _applicationHost.Services.GetRequiredService<WorkspaceViewModel>().RestoreTerminalWorkspaceAsync();

            var window = _applicationHost.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _ = CheckForApplicationUpdatesAsync();
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("STARTUP", "Application startup failed.", exception);

            MessageBox.Show(
                $"OnlyShell 无法启动。\n\n{exception.Message}\n\n详细信息已写入：{ApplicationDiagnostics.CurrentLogPath}",
                "OnlyShell / Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApplicationDiagnostics.Write("APP", $"Application exiting with code {e.ApplicationExitCode}.");
        _applicationHost?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        ApplicationDiagnostics.Write("UI-UNHANDLED", "Unhandled UI dispatcher exception.", e.Exception);

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        ApplicationDiagnostics.Write("PROCESS-UNHANDLED", $"Unhandled process exception; terminating={e.IsTerminating}.", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        ApplicationDiagnostics.Write("TASK-UNOBSERVED", "Unobserved task exception.", e.Exception);

    private static string GetDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnlyShell",
        "onlyshell.db");

    private static async Task CheckForApplicationUpdatesAsync()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource("https://github.com/corizhang/OnlyShell", null, prerelease: false));
            if (!manager.IsInstalled || !IsApplicationUpdateCheckDue())
                return;

            var update = await manager.CheckForUpdatesAsync();
            RecordApplicationUpdateCheck();
            if (update is null)
                return;

            var version = update.TargetFullRelease.Version.ToString();
            var choice = MessageBox.Show(
                $"发现 OnlyShell 新版本 {version}，是否现在下载并重启完成更新？",
                "OnlyShell / Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice != MessageBoxResult.Yes)
                return;

            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("UPDATE", "Could not check for or apply application updates.", exception);
        }
    }

    private static bool IsApplicationUpdateCheckDue()
    {
        var checkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnlyShell",
            "last-update-check.txt");

        try
        {
            if (File.Exists(checkPath)
                && DateTimeOffset.TryParse(File.ReadAllText(checkPath), out var lastCheck)
                && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(24))
            {
                return false;
            }

        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("UPDATE", "Could not read the update check time; continuing with the check.", exception);
        }

        return true;
    }

    private static void RecordApplicationUpdateCheck()
    {
        var checkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OnlyShell",
            "last-update-check.txt");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkPath)!);
            File.WriteAllText(checkPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("UPDATE", "Could not persist the update check time.", exception);
        }
    }
}
