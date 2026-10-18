using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using OnlyShell.Presentation.ViewModels;

namespace OnlyShell.App;

public partial class QuickCommandsWindow : Window
{
    private readonly TerminalTabViewModel _terminalTab;
    private readonly JsonQuickCommandStore _store = new();
    private readonly ObservableCollection<QuickCommandDefinition> _commands = [];
    private readonly ObservableCollection<QuickCommandVariable> _variables = [];
    private readonly ICollectionView _commandsView;
    private readonly Dictionary<string, Control> _variableInputs = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _selectedId;
    private bool _loadingSelection;

    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    public QuickCommandsWindow(TerminalTabViewModel terminalTab)
    {
        InitializeComponent();
        _terminalTab = terminalTab;
        _commandsView = CollectionViewSource.GetDefaultView(_commands);
        _commandsView.Filter = MatchesSearch;
        CommandsList.ItemsSource = _commandsView;
        VariablesGrid.ItemsSource = _variables;
        Loaded += async (_, _) => await LoadCommandsAsync();
    }

    private async Task LoadCommandsAsync()
    {
        try
        {
            var loaded = await _store.LoadAsync();
            foreach (var command in loaded.OrderBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                _commands.Add(command);
            _commandsView.Refresh();
            StatusText.Text = $"{_commands.Count} SAVED COMMANDS · LOCAL ONLY";
            if (_commands.Count > 0)
                CommandsList.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"LOAD FAILED · {exception.Message}";
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not QuickCommandDefinition command)
            return false;
        var query = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(query)
            || command.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || command.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (command.Variables ?? []).Any(variable => variable.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || variable.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _commandsView.Refresh();

    private void CommandsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection)
            return;
        if (CommandsList.SelectedItem is not QuickCommandDefinition command)
            return;
        _selectedId = command.Id;
        NameBox.Text = command.Name;
        CategoryBox.Text = command.Category;
        DescriptionBox.Text = command.Description;
        CommandBox.Text = command.Command;
        _loadingSelection = true;
        _variables.Clear();
        foreach (var variable in command.Variables ?? [])
            _variables.Add(Clone(variable));
        _loadingSelection = false;
        RebuildVariableInputs();
        UpdatePreview();
        StatusText.Text = $"SELECTED · {command.Category}";
    }

    private void NewCommand_Click(object sender, RoutedEventArgs e)
    {
        _selectedId = null;
        CommandsList.SelectedItem = null;
        NameBox.Text = string.Empty;
        CategoryBox.Text = "General";
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
            string.IsNullOrWhiteSpace(CategoryBox.Text) ? "General" : CategoryBox.Text.Trim(),
            commandText,
            DescriptionBox.Text.Trim())
        {
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
            _commandsView.Refresh();
            _loadingSelection = true;
            CommandsList.SelectedItem = _commands.FirstOrDefault(item => item.Id == id);
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
        NameBox.Clear();
        CategoryBox.Clear();
        DescriptionBox.Clear();
        CommandBox.Clear();
        _variables.Clear();
        RebuildVariableInputs();
        UpdatePreview();
        try
        {
            await PersistAsync();
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
            var field = new StackPanel { Width = 190, Margin = new Thickness(0, 0, 8, 5) };
            field.Children.Add(new TextBlock
            {
                Text = $"{(string.IsNullOrWhiteSpace(variable.Label) ? variable.Name : variable.Label)}{(variable.Required ? " *" : "")}{(variable.Secret ? " · SECRET" : "")}",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
                FontSize = 8,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Control input;
            if (variable.Secret)
            {
                var password = new PasswordBox { Height = 25, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0), Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush") };
                password.Password = "";
                password.PasswordChanged += (_, _) => UpdatePreview();
                input = password;
            }
            else
            {
                var remembered = variable.Secret || !variable.RememberValue ? "" : variable.LastValue;
                var textBox = new TextBox { Height = 25, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0), Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush"), Text = previousValues.GetValueOrDefault(variable.Name.Trim()) ?? (remembered.Length > 0 ? remembered : variable.DefaultValue) };
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
        PreviewText.Foreground = (System.Windows.Media.Brush)FindResource(error is null ? "SyntaxGreenBrush" : "AccentBrush");
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

    private int FindCommandIndex(Guid id)
    {
        for (var index = 0; index < _commands.Count; index++)
            if (_commands[index].Id == id)
                return index;
        return -1;
    }

    private Task PersistAsync() => _store.SaveAsync(_commands.ToArray());
}
