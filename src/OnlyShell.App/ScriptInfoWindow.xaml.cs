using System.Windows;
using System.Windows.Controls;

namespace OnlyShell.App;

public partial class ScriptInfoWindow : Window
{
    private readonly string[] _folders;

    public string ScriptName => NameBox.Text.Trim();
    public string ScriptDescription => DescriptionBox.Text.Trim();
    public string ScriptFolderPath { get; private set; }
    public string ScriptRuntime => RuntimeBox.SelectedItem as string ?? "PowerShell";
    public string ExecutionTarget => TargetBox.SelectedItem as string ?? "本机";

    public ScriptInfoWindow(
        string name,
        string description,
        string folderPath,
        string runtime,
        string executionTarget,
        IEnumerable<string> folders)
    {
        InitializeComponent();
        _folders = folders.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ScriptFolderPath = folderPath;
        NameBox.Text = name;
        DescriptionBox.Text = description;
        FolderPathText.Text = string.IsNullOrWhiteSpace(folderPath) ? "/ (根目录)" : $"/ {folderPath}";
        TargetBox.ItemsSource = new[] { "远程主机", "本机" };
        TargetBox.SelectionChanged += TargetBox_SelectionChanged;
        TargetBox.SelectedItem = executionTarget;
        UpdateRuntimeOptions(runtime);
    }

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRuntimeOptions(RuntimeBox.SelectedItem as string);

    private void UpdateRuntimeOptions(string? preferredRuntime)
    {
        var options = ExecutionTarget == "本机"
            ? new[] { "PowerShell", "CMD", "Python 3" }
            : new[] { "Bash", "Python 3" };
        RuntimeBox.ItemsSource = options;
        RuntimeBox.SelectedItem = options.Contains(preferredRuntime, StringComparer.Ordinal) ? preferredRuntime : options[0];
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPickerWindow(_folders, ScriptFolderPath) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                ScriptFolderPath = picker.SelectedPath;
                FolderPathText.Text = string.IsNullOrWhiteSpace(ScriptFolderPath) ? "/ (根目录)" : $"/ {ScriptFolderPath}";
            }
        }
        catch (Exception exception)
        {
            ValidationText.Text = $"目录选择失败：{exception.Message}";
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ValidationText.Text = "请填写脚本名称。";
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
