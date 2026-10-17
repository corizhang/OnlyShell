using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OnlyShell.Core.Scripts;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class ScriptCatalogNode : ObservableObject
{
    public ScriptCatalogNode(string name, string folderPath, ScriptDefinition? script = null)
    {
        Name = name;
        FolderPath = folderPath;
        Script = script;
        IsExpanded = script is null;
    }

    public string Name { get; }
    public string FolderPath { get; }
    public ScriptDefinition? Script { get; }
    public bool IsFolder => Script is null;
    public string ExpandGlyph => IsFolder ? IsExpanded ? "▾" : "▸" : string.Empty;
    public double TreeIndentWidth => Depth * 14;
    public int Depth { get; private set; }
    public string Detail => Script is null
        ? $"{Children.Count} ITEM{(Children.Count == 1 ? string.Empty : "S")}"
        : $"{Script.Runtime} · {Script.Description}";
    public ObservableCollection<ScriptCatalogNode> Children { get; } = [];

    public void SetTreePresentation(int depth, IReadOnlyList<bool> ancestorHasFollowingSibling, bool hasFollowingSibling)
    {
        Depth = depth;
        OnPropertyChanged(nameof(Depth));
        OnPropertyChanged(nameof(TreeIndentWidth));
    }

    [ObservableProperty]
    private bool isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

}
