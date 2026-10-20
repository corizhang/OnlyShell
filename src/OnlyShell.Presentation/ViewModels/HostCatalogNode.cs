using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OnlyShell.Core.Hosts;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class HostCatalogNode(string name, string groupPath, HostProfile? host = null) : ObservableObject
{
    public string Name { get; } = name;
    public string GroupPath { get; } = groupPath;
    public HostProfile? Host { get; } = host;
    public bool IsFolder => Host is null;
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public double TreeIndentWidth => Depth * 14;
    public int Depth { get; private set; }
    public string Detail => Host is null ? $"{Children.Count} HOST{(Children.Count == 1 ? "" : "S")}" : Host.HostName;
    public ObservableCollection<HostCatalogNode> Children { get; } = [];

    public void SetDepth(int depth)
    {
        Depth = depth;
        OnPropertyChanged(nameof(TreeIndentWidth));
    }

    [ObservableProperty]
    private bool isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));
}
