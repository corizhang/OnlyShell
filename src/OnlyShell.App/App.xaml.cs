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

namespace OnlyShell.App;

public partial class App : Application
{
    private IHost? _applicationHost;

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
}
