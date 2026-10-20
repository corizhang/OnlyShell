using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace OnlyShell.App;

public sealed class FolderPickerNode(string name, string path) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public ObservableCollection<FolderPickerNode> Children { get; } = [];
    private bool _isExpanded = true;
    private bool _isSelected;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class FolderPickerWindow : Window
{
    private readonly FolderPickerNode _root = new("根目录", "");
    private FolderPickerNode _selected;

    public string SelectedPath { get; private set; } = "";

    public FolderPickerWindow(IEnumerable<string> existingPaths, string? initialPath = null)
    {
        InitializeComponent();
        _selected = _root;
        FolderTree.ItemsSource = new[] { _root };
        foreach (var path in existingPaths.Append(initialPath ?? ""))
            AddPath(path);
        UpdateSelectedPath();
        if (!string.IsNullOrWhiteSpace(initialPath))
            Loaded += (_, _) => SelectPath(initialPath);
    }

    private void AddPath(string path)
    {
        var parent = _root;
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var accumulated = new List<string>();
        foreach (var segment in segments)
        {
            accumulated.Add(segment);
            var fullPath = string.Join('/', accumulated);
            var child = parent.Children.FirstOrDefault(item => item.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                child = new FolderPickerNode(segment, fullPath);
                parent.Children.Add(child);
            }
            parent = child;
        }
    }

    private void SelectPath(string path)
    {
        var current = _root;
        current.IsExpanded = true;
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var child = current.Children.FirstOrDefault(item => item.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) return;
            current.IsExpanded = true;
            current = child;
        }
        _selected = current;
        current.IsSelected = true;
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderPickerNode node)
        {
            if (!ReferenceEquals(_selected, node))
            {
                _selected.IsSelected = false;
                _selected = node;
            }
            UpdateSelectedPath();
        }
    }

    private void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = NewFolderNameBox.Text.Trim().Trim('/', '\\');
        if (name.Length == 0)
            return;
        if (name.Contains('/') || name.Contains('\\') || name is "." or "..")
        {
            MessageBox.Show(this, "请输入单个文件夹名称。", "OnlyShell / Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selected.Children.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "当前目录下已存在同名文件夹。", "OnlyShell / Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = string.IsNullOrEmpty(_selected.Path) ? name : $"{_selected.Path}/{name}";
        var node = new FolderPickerNode(name, path);
        _selected.Children.Add(node);
        _selected.IsExpanded = true;
        _selected.IsSelected = false;
        _selected = node;
        _selected.IsSelected = true;
        NewFolderNameBox.Clear();
        UpdateSelectedPath();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = _selected.Path;
        DialogResult = true;
    }

    private void UpdateSelectedPath() => SelectedPathText.Text = string.IsNullOrEmpty(_selected.Path) ? "/ (根目录)" : $"/ {_selected.Path}";
}
