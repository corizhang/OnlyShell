using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Files;
using OnlyShell.Core.Security;
using OnlyShell.Core.Secrets;
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

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<SqliteHostRepository>(_ => new SqliteHostRepository(GetDatabasePath()));
            builder.Services.AddSingleton<IHostRepository>(provider => provider.GetRequiredService<SqliteHostRepository>());
            builder.Services.AddSingleton<IKnownHostRepository>(_ => new SqliteKnownHostRepository(GetDatabasePath()));
            builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
            builder.Services.AddSingleton<ISshTerminalConnector, SshTerminalConnector>();
            builder.Services.AddSingleton<ISftpSessionConnector, SftpSessionConnector>();
            builder.Services.AddSingleton<IHostKeyConfirmationService, WpfHostKeyConfirmationService>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.IConnectionCredentialPrompt, WpfConnectionCredentialPrompt>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.IHostEditorDialogService, WpfHostEditorDialogService>();
            builder.Services.AddSingleton<OnlyShell.Presentation.Services.ISftpDialogService, WpfSftpDialogService>();
            builder.Services.AddSingleton<HttpClient>();
            builder.Services.AddSingleton<ISyncInteractionService, WpfSyncInteractionService>();
            builder.Services.AddSingleton<IGitHubSyncService, GitHubSyncService>();
            builder.Services.AddSingleton<WorkspaceViewModel>();
            builder.Services.AddTransient<MainWindow>();
            _applicationHost = builder.Build();

            await _applicationHost.Services.GetRequiredService<SqliteHostRepository>().InitializeAsync();
            await _applicationHost.Services.GetRequiredService<WorkspaceViewModel>().LoadAsync();

            var window = _applicationHost.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"OnlyShell 无法启动。\n\n{exception.Message}",
                "OnlyShell / Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _applicationHost?.Dispose();
        base.OnExit(e);
    }

    private static string GetDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnlyShell",
        "onlyshell.db");
}
