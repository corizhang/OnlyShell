using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OnlyShell.Presentation.ViewModels;

namespace OnlyShell.App;

public partial class QuickCommandsWindow : Window
{
    private readonly TerminalTabViewModel _terminalTab;
    private readonly JsonQuickCommandStore _store = new();
    private readonly ObservableCollection<QuickCommandDefinition> _commands = [];
    private readonly ObservableCollection<QuickCommandVariable> _variables = [];
    private readonly ObservableCollection<CommandTreeNode> _visibleCommandNodes = [];
    private readonly Dictionary<string, Control> _variableInputs = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _selectedId;
    private string _folderPath = "General";
    private bool _loadingSelection;

    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private sealed class CommandTreeNode(string name, string path, QuickCommandDefinition? command = null)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public QuickCommandDefinition? Command { get; } = command;
        public bool IsFolder => Command is null;
        public string KindGlyph => IsFolder ? "▰" : "▤";
        public string ExpandGlyph => IsExpanded ? "▾" : "▸";
        public double IndentWidth => Depth * 14;
        public int Depth { get; set; }
        public bool IsExpanded { get; set; } = command is null;
        public string Detail => Command is null
            ? $"{Children.Count} ITEM{(Children.Count == 1 ? "" : "S")}"
            : Command.Description;
        public List<CommandTreeNode> Children { get; } = [];
    }

    public QuickCommandsWindow(TerminalTabViewModel terminalTab)
    {
        InitializeComponent();
        _terminalTab = terminalTab;
        CommandsList.ItemsSource = _visibleCommandNodes;
        VariablesGrid.ItemsSource = _variables;
        Loaded += async (_, _) => await LoadCommandsAsync();
    }

    private async Task LoadCommandsAsync()
    {
        try
        {
            var loaded = await _store.LoadAsync();
            foreach (var command in loaded.Select(MigrateFolderPath)
                         .OrderBy(item => item.FolderPath, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                _commands.Add(command);
            RebuildCommandTree();
            StatusText.Text = $"{_commands.Count} SAVED COMMANDS · LOCAL ONLY";
            if (_commands.Count > 0)
                CommandsList.SelectedItem = _visibleCommandNodes.FirstOrDefault(node => node.Command is not null);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"LOAD FAILED · {exception.Message}";
        }
    }

    private bool MatchesSearch(QuickCommandDefinition command)
    {
        var query = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(query)
            || command.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.FolderPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (command.Variables ?? []).Any(variable => variable.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || variable.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildCommandTree();

    private void CommandsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection)
            return;
        if (CommandsList.SelectedItem is not CommandTreeNode { Command: { } command })
            return;
        _selectedId = command.Id;
        NameBox.Text = command.Name;
        _folderPath = command.FolderPath;
        UpdateFolderPathText();
        DescriptionBox.Text = command.Description;
        CommandBox.Text = command.Command;
        _loadingSelection = true;
        _variables.Clear();
        foreach (var variable in command.Variables ?? [])
            _variables.Add(Clone(variable));
        _loadingSelection = false;
        RebuildVariableInputs();
        UpdatePreview();
        StatusText.Text = $"SELECTED · {(_folderPath.Length == 0 ? "ROOT" : _folderPath)}";
    }

    private void NewCommand_Click(object sender, RoutedEventArgs e)
    {
        _selectedId = null;
        CommandsList.SelectedItem = null;
        NameBox.Text = string.Empty;
        _folderPath = "General";
        UpdateFolderPathText();
        DescriptionBox.Text = string.Empty;
        CommandBox.Text = string.Empty;
        _variables.Clear();
        RebuildVariableInputs();
        UpdatePreview();
        NameBox.Focus();
        StatusText.Text = "NEW COMMAND · FILL IN AND SAVE";
    }

    private async void SaveCommand_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var commandText = CommandBox.Text;
        if (name.Length == 0 || string.IsNullOrWhiteSpace(commandText))
        {
            StatusText.Text = "NAME AND COMMAND ARE REQUIRED";
            return;
        }
        if (_commands.Any(command => command.Id != _selectedId && command.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = "A COMMAND WITH THIS NAME ALREADY EXISTS";
            return;
        }
        if (commandText.Contains('\r') || commandText.Contains('\n'))
        {
            StatusText.Text = "ONLY SINGLE-LINE COMMANDS CAN BE SAVED";
            return;
        }
        var placeholderRemainder = PlaceholderRegex.Replace(commandText, "");
        if (placeholderRemainder.Contains("{{", StringComparison.Ordinal) || placeholderRemainder.Contains("}}", StringComparison.Ordinal))
        {
            StatusText.Text = "INVALID PLACEHOLDER · USE {{name}} WITH LETTERS, DIGITS OR _";
            return;
        }
        var duplicate = _variables.GroupBy(variable => variable.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Key.Length > 0 && group.Count() > 1);
        if (duplicate is not null || _variables.Any(variable => !Regex.IsMatch(variable.Name.Trim(), "^[A-Za-z_][A-Za-z0-9_]*$")))
        {
            StatusText.Text = "VARIABLE NAMES MUST BE UNIQUE IDENTIFIERS (A-Z, 0-9, _)";
            return;
        }
        var definedNames = _variables.Select(variable => variable.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownNames = PlaceholderRegex.Matches(commandText).Select(match => match.Groups[1].Value)
            .Where(name => !definedNames.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknownNames.Length > 0)
        {
            StatusText.Text = $"ADD VARIABLE DEFINITIONS FOR: {string.Join(", ", unknownNames)}";
            return;
        }

        var id = _selectedId ?? Guid.NewGuid();
        var definition = new QuickCommandDefinition(
            id,
            name,
            _folderPath.Length == 0 ? "General" : _folderPath,
            commandText,
            DescriptionBox.Text.Trim())
        {
            FolderPath = NormalizeFolderPath(_folderPath),
            Variables = _variables.Select(variable =>
            {
                var saved = Clone(variable);
                var value = _variableInputs.TryGetValue(saved.Name.Trim(), out var input) ? ReadValue(input) : saved.LastValue;
                saved.LastValue = !saved.Secret && saved.RememberValue ? value : "";
                if (saved.Secret)
                    saved.DefaultValue = "";
                return saved;
            }).ToList()
        };
        var existingIndex = FindCommandIndex(id);
        if (existingIndex >= 0)
            _commands[existingIndex] = definition;
        else
            _commands.Add(definition);
        _selectedId = id;

        try
        {
            await PersistAsync();
            RebuildCommandTree();
            _loadingSelection = true;
            CommandsList.SelectedItem = _visibleCommandNodes.FirstOrDefault(item => item.Command?.Id == id);
            _loadingSelection = false;
            StatusText.Text = "SAVED · LOCAL ONLY";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"SAVE FAILED · {exception.Message}";
        }
    }

    private async void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not Guid id)
        {
            StatusText.Text = "SELECT A SAVED COMMAND FIRST";
            return;
        }
        var selected = _commands.FirstOrDefault(item => item.Id == id);
        if (selected is null)
            return;
        if (MessageBox.Show(this, $"删除快捷命令“{selected.Name}”？", "OnlyShell / Delete Command", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _commands.Remove(selected);
        _selectedId = null;
        CommandsList.SelectedItem = null;
        NameBox.Clear();
        _folderPath = "";
        UpdateFolderPathText();
        DescriptionBox.Clear();
        CommandBox.Clear();
        _variables.Clear();
        RebuildVariableInputs();
        UpdatePreview();
        try
        {
            await PersistAsync();
            RebuildCommandTree();
            StatusText.Text = "DELETED · LOCAL ONLY";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"DELETE FAILED · {exception.Message}";
        }
    }

    private async void InsertCommand_Click(object sender, RoutedEventArgs e)
    {
        var commandText = RenderCommand(redactSecrets: false, out var error);
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }
        if (string.IsNullOrWhiteSpace(commandText))
        {
            StatusText.Text = "COMMAND IS EMPTY";
            return;
        }
        if (commandText.Contains('\r') || commandText.Contains('\n'))
        {
            StatusText.Text = "ONLY SINGLE-LINE COMMANDS CAN BE INSERTED";
            return;
        }
        if (!_terminalTab.IsConnected || _terminalTab.Session is not { } session)
        {
            StatusText.Text = "TERMINAL IS NOT CONNECTED";
            return;
        }

        session.Write(session.InputEncoding.GetBytes(commandText));
        await PersistRememberedValuesAsync();
        Close();
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        var index = 1;
        while (_variables.Any(variable => variable.Name.Equals($"var{index}", StringComparison.OrdinalIgnoreCase)))
            index++;
        var variable = new QuickCommandVariable { Name = $"var{index}", Label = $"Variable {index}", Required = true };
        _variables.Add(variable);
        CommandBox.Text = string.IsNullOrWhiteSpace(CommandBox.Text)
            ? $"{{{{{variable.Name}}}}}"
            : $"{CommandBox.Text} {{{{{variable.Name}}}}}";
        VariablesGrid.SelectedItem = variable;
        RebuildVariableInputs();
        UpdatePreview();
        VariablesGrid.ScrollIntoView(variable);
        VariablesGrid.BeginEdit();
    }

    private void RemoveVariable_Click(object sender, RoutedEventArgs e)
    {
        if (VariablesGrid.SelectedItem is QuickCommandVariable variable)
        {
            _variables.Remove(variable);
            RebuildVariableInputs();
            UpdatePreview();
        }
    }

    private void CommandBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void VariablesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_loadingSelection) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is QuickCommandVariable { Secret: true } secretVariable)
            {
                secretVariable.RememberValue = false;
                secretVariable.DefaultValue = "";
                secretVariable.LastValue = "";
            }
            RebuildVariableInputs();
            UpdatePreview();
        }, DispatcherPriority.Background);
    }

    private void RebuildVariableInputs()
    {
        var previousValues = _variableInputs.ToDictionary(pair => pair.Key, pair => ReadValue(pair.Value), StringComparer.OrdinalIgnoreCase);
        VariableInputsPanel.Children.Clear();
        _variableInputs.Clear();
        foreach (var variable in _variables.Where(item => Regex.IsMatch(item.Name.Trim(), "^[A-Za-z_][A-Za-z0-9_]*$")))
        {
            var field = new StackPanel
            {
                Width = (double)FindResource("Control.QuickCommand.VariableFieldWidth"),
                Margin = (Thickness)FindResource("Space.QuickCommand.VariableField.Margin")
            };
            field.Children.Add(new TextBlock
            {
                Text = $"{(string.IsNullOrWhiteSpace(variable.Label) ? variable.Name : variable.Label)}{(variable.Required ? " *" : "")}{(variable.Secret ? " · SECRET" : "")}",
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Mono"),
                FontSize = (double)FindResource("Type.Size.Caption"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Control input;
            if (variable.Secret)
            {
                var password = new PasswordBox { Height = (double)FindResource("Control.InputHeight.Dense"), Padding = (Thickness)FindResource("Space.Input.Padding.Dense"), Margin = (Thickness)FindResource("Space.Gap.Top.XXS"), Background = (System.Windows.Media.Brush)FindResource("Brush.Surface.Input"), Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Primary"), BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Border.Subtle") };
                password.Password = "";
                password.PasswordChanged += (_, _) => UpdatePreview();
                input = password;
            }
            else
            {
                var remembered = variable.Secret || !variable.RememberValue ? "" : variable.LastValue;
                var textBox = new TextBox { Height = (double)FindResource("Control.InputHeight.Dense"), Padding = (Thickness)FindResource("Space.Input.Padding.Dense"), Margin = (Thickness)FindResource("Space.Gap.Top.XXS"), Background = (System.Windows.Media.Brush)FindResource("Brush.Surface.Input"), Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Primary"), BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Border.Subtle"), Text = previousValues.GetValueOrDefault(variable.Name.Trim()) ?? (remembered.Length > 0 ? remembered : variable.DefaultValue) };
                textBox.TextChanged += (_, _) => UpdatePreview();
                input = textBox;
            }
            field.Children.Add(input);
            VariableInputsPanel.Children.Add(field);
            _variableInputs[variable.Name.Trim()] = input;
        }
    }

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        var rendered = RenderCommand(redactSecrets: true, out var error);
        PreviewText.Text = error ?? rendered;
        PreviewText.Foreground = (System.Windows.Media.Brush)FindResource(error is null ? "Brush.Status.Success" : "Brush.Accent.Primary");
    }

    private string RenderCommand(bool redactSecrets, out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var referencedNames = PlaceholderRegex.Matches(CommandBox.Text).Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in _variables)
        {
            var name = variable.Name.Trim();
            if (name.Length == 0 || !referencedNames.Contains(name)) continue;
            var value = _variableInputs.TryGetValue(name, out var control) ? ReadValue(control) : variable.DefaultValue;
            if (variable.Required && string.IsNullOrWhiteSpace(value))
            {
                error = $"REQUIRED VALUE MISSING · {(variable.Label.Trim() is { Length: > 0 } label ? label : name)}";
                return CommandBox.Text;
            }
            values[name] = redactSecrets && variable.Secret && value.Length > 0 ? "••••" : value;
        }

        var withoutValidPlaceholders = PlaceholderRegex.Replace(CommandBox.Text, "");
        if (withoutValidPlaceholders.Contains("{{", StringComparison.Ordinal) || withoutValidPlaceholders.Contains("}}", StringComparison.Ordinal))
        {
            error = "INVALID PLACEHOLDER · USE {{name}} WITH LETTERS, DIGITS OR _";
            return CommandBox.Text;
        }

        var unknown = PlaceholderRegex.Matches(CommandBox.Text).Select(match => match.Groups[1].Value)
            .FirstOrDefault(name => !values.ContainsKey(name));
        if (unknown is not null)
        {
            error = $"VARIABLE NOT DEFINED · {unknown}";
            return CommandBox.Text;
        }
        error = null;
        return PlaceholderRegex.Replace(CommandBox.Text, match => values.GetValueOrDefault(match.Groups[1].Value) ?? "");
    }

    private async Task PersistRememberedValuesAsync()
    {
        if (_selectedId is not Guid id)
            return;
        var index = FindCommandIndex(id);
        if (index < 0)
            return;
        var savedCommand = _commands[index];
        var updatedVariables = (savedCommand.Variables ?? []).Select(variable =>
        {
            var copy = Clone(variable);
            var currentDefinition = _variables.FirstOrDefault(item => item.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase));
            var secret = currentDefinition?.Secret ?? copy.Secret;
            var remember = currentDefinition?.RememberValue ?? copy.RememberValue;
            copy.LastValue = !secret && remember && _variableInputs.TryGetValue(copy.Name.Trim(), out var input)
                ? ReadValue(input)
                : "";
            return copy;
        }).ToList();
        _commands[index] = savedCommand with { Variables = updatedVariables };
        try
        {
            await PersistAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"INSERTED · VALUE SAVE FAILED · {exception.Message}";
        }
    }

    private static string ReadValue(Control control) => control switch
    {
        TextBox textBox => textBox.Text,
        PasswordBox passwordBox => passwordBox.Password,
        _ => ""
    };

    private static QuickCommandVariable Clone(QuickCommandVariable variable) => new()
    {
        Name = variable.Name,
        Label = variable.Label,
        DefaultValue = variable.Secret ? "" : variable.DefaultValue,
        Required = variable.Required,
        Secret = variable.Secret,
        RememberValue = variable.Secret ? false : variable.RememberValue,
        LastValue = variable.Secret ? "" : variable.LastValue
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChooseCommandFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPickerWindow(_commands.Select(command => command.FolderPath), _folderPath) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                _folderPath = picker.SelectedPath;
                UpdateFolderPathText();
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("FOLDER_PICKER", "Could not choose a quick command folder.", exception);
            MessageBox.Show(this, $"文件夹选择失败：{exception.Message}", "OnlyShell / Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleCommandFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandTreeNode { IsFolder: true } node)
            return;
        node.IsExpanded = !node.IsExpanded;
        RebuildCommandTree();
    }

    private void UpdateFolderPathText() => FolderPathText.Text = string.IsNullOrWhiteSpace(_folderPath) ? "/ (根目录)" : $"/ {_folderPath}";

    private void RebuildCommandTree()
    {
        var expansion = EnumerateNodes(_visibleCommandNodes)
            .Where(node => node.IsFolder)
            .ToDictionary(node => node.Path, node => node.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var roots = new List<CommandTreeNode>();
        var folders = new Dictionary<string, CommandTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in _commands.Where(MatchesSearch))
        {
            var parent = roots;
            var segments = NormalizeFolderPath(command.FolderPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var pathSegments = new List<string>();
            foreach (var segment in segments)
            {
                pathSegments.Add(segment);
                var path = string.Join('/', pathSegments);
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new CommandTreeNode(segment, path)
                    {
                        IsExpanded = SearchBox.Text.Trim().Length > 0 || !expansion.TryGetValue(path, out var expanded) || expanded
                    };
                    folders.Add(path, folder);
                    parent.Add(folder);
                }
                parent = folder.Children;
            }
            parent.Add(new CommandTreeNode(command.Name, command.FolderPath, command));
        }
        SortNodes(roots);
        _visibleCommandNodes.Clear();
        AddVisibleNodes(roots, 0);
    }

    private void AddVisibleNodes(IReadOnlyList<CommandTreeNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            node.Depth = depth;
            _visibleCommandNodes.Add(node);
            if (node.IsFolder && node.IsExpanded)
                AddVisibleNodes(node.Children, depth + 1);
        }
    }

    private static IEnumerable<CommandTreeNode> EnumerateNodes(IEnumerable<CommandTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
                yield return child;
        }
    }

    private static void SortNodes(List<CommandTreeNode> nodes)
    {
        nodes.Sort((left, right) =>
        {
            var folderOrder = right.IsFolder.CompareTo(left.IsFolder);
            return folderOrder != 0 ? folderOrder : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        foreach (var node in nodes)
            SortNodes(node.Children);
    }

    private static string NormalizeFolderPath(string path) => string.Join('/',
        (path ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not "." and not ".."));

    private static QuickCommandDefinition MigrateFolderPath(QuickCommandDefinition command) => string.IsNullOrWhiteSpace(command.FolderPath)
        ? command with { FolderPath = NormalizeFolderPath(command.Category == "General" ? "General" : command.Category) }
        : command with { FolderPath = NormalizeFolderPath(command.FolderPath) };

    private int FindCommandIndex(Guid id)
    {
        for (var index = 0; index < _commands.Count; index++)
            if (_commands[index].Id == id)
                return index;
        return -1;
    }

    private Task PersistAsync() => _store.SaveAsync(_commands.ToArray());
}
