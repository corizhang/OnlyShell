using CommunityToolkit.Mvvm.ComponentModel;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class SftpTransferItemViewModel(string name, string direction, string remotePath) : ObservableObject
{
    public string Name { get; } = name;
    public string Direction { get; } = direction;
    public string RemotePath { get; } = remotePath;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string status = "QUEUED";

    [ObservableProperty]
    private string? errorMessage;

    public string ProgressLabel => $"{Progress:0}%";

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressLabel));
}
