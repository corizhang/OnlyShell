using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OnlyShell.Core.Scripts;

namespace OnlyShell.App;

public partial class ScriptRunInputsWindow : Window
{
    private static readonly Regex EnvironmentNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private readonly List<ArgumentRow> _argumentRows = [];
    private readonly List<EnvironmentRow> _environmentRows = [];
    private string _lastArgumentGroup = string.Empty;
    private string _lastEnvironmentGroup = string.Empty;

    public ScriptRunInputsWindow(ScriptExecutionInputs initialInputs, IReadOnlyList<ScriptInputFieldDefinition> fields)
    {
        InitializeComponent();
        var argumentFields = fields.Where(field => field.Kind == "ARG").ToArray();
        var envFields = fields.Where(field => field.Kind == "ENV").ToArray();
        for (var index = 0; index < argumentFields.Length; index++)
            AddArgumentRow(argumentFields[index], index < initialInputs.Arguments.Count ? initialInputs.Arguments[index] : string.Empty);
        for (var index = argumentFields.Length; index < initialInputs.Arguments.Count; index++)
            AddArgumentRow(null, initialInputs.Arguments[index]);

        var envValues = new Dictionary<string, string>(initialInputs.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var field in envFields)
        {
            envValues.Remove(field.Name);
            initialInputs.EnvironmentVariables.TryGetValue(field.Name, out var value);
            AddEnvironmentRow(field, value ?? string.Empty);
        }
        foreach (var pair in envValues.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            AddEnvironmentRow(null, pair.Value, pair.Key);
    }

    public ScriptExecutionInputs? Inputs { get; private set; }

    private void AddArgument_Click(object sender, RoutedEventArgs e) => AddArgumentRow(null, string.Empty);
    private void AddEnvironment_Click(object sender, RoutedEventArgs e) => AddEnvironmentRow(null, string.Empty);

    private void AddArgumentRow(ScriptInputFieldDefinition? definition, string value)
    {
        AddGroupHeader(ArgumentsPanel, definition?.Group, ref _lastArgumentGroup);
        var label = new TextBlock
        {
            Text = definition is null ? $"参数 {_argumentRows.Count + 1}" : definition.Name,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(definition is null ? "MutedBrush" : "SyntaxCyanBrush"),
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = definition?.Name
        };
        var valueControl = CreateValueControl(definition, value);
        var grid = CreateRowGrid();
        grid.Children.Add(label);
        Grid.SetColumn(valueControl.Root, 1);
        grid.Children.Add(valueControl.Root);
        AddRemoveButton(grid, () => { _argumentRows.RemoveAll(row => ReferenceEquals(row.Grid, grid)); ArgumentsPanel.Children.Remove(grid); RefreshArgumentLabels(); });
        _argumentRows.Add(new ArgumentRow(grid, label, definition, valueControl));
        ArgumentsPanel.Children.Add(grid);
    }

    private void AddEnvironmentRow(ScriptInputFieldDefinition? definition, string value, string name = "")
    {
        AddGroupHeader(EnvironmentPanel, definition?.Group, ref _lastEnvironmentGroup);
        name = definition?.Name ?? name;
        var nameBox = CreateTextBox(name);
        nameBox.IsReadOnly = definition is not null;
        if (definition is not null)
            nameBox.Foreground = (Brush)FindResource("SyntaxCyanBrush");
        var valueControl = CreateValueControl(definition, value);
        var grid = CreateRowGrid();
        grid.Children.Add(nameBox);
        Grid.SetColumn(valueControl.Root, 1);
        grid.Children.Add(valueControl.Root);
        AddRemoveButton(grid, () => { _environmentRows.RemoveAll(row => ReferenceEquals(row.Grid, grid)); EnvironmentPanel.Children.Remove(grid); });
        _environmentRows.Add(new EnvironmentRow(grid, nameBox, definition, valueControl));
        EnvironmentPanel.Children.Add(grid);
    }

    private void AddGroupHeader(Panel panel, string? group, ref string lastGroup)
    {
        group = group?.Trim() ?? string.Empty;
        if (group.Length == 0)
        {
            lastGroup = string.Empty;
            return;
        }
        if (group.Equals(lastGroup, StringComparison.OrdinalIgnoreCase))
            return;
        var header = new TextBlock
        {
            Text = $"// {group.ToUpperInvariant()}",
            Foreground = (Brush)FindResource("SyntaxCyanBrush"),
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 9,
            Margin = new Thickness(0, panel.Children.Count == 0 ? 2 : 10, 0, 5)
        };
        panel.Children.Add(header);
        lastGroup = group;
    }

    private Grid CreateRowGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private ValueControl CreateValueControl(ScriptInputFieldDefinition? definition, string value)
    {
        var valueType = definition?.ValueType ?? ScriptInputValueType.Text;
        var isSensitive = definition?.IsSensitive == true || valueType == ScriptInputValueType.Secret;
        if (isSensitive)
            return CreateSensitiveValueControl(value);
        if (valueType == ScriptInputValueType.Boolean)
        {
            var combo = CreateComboBox(["", "true", "false"], value);
            return new ValueControl(combo, () => combo.SelectedItem as string ?? string.Empty);
        }
        if (valueType == ScriptInputValueType.Choice)
        {
            var options = (definition?.Options ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!definition?.IsRequired ?? false)
                options.Insert(0, string.Empty);
            if (value.Length > 0 && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
                options.Add(value);
            var combo = CreateComboBox(options, value);
            return new ValueControl(combo, () => combo.SelectedItem as string ?? string.Empty);
        }

        var textBox = CreateTextBox(value);
        return new ValueControl(textBox, () => textBox.Text);
    }

    private ValueControl CreateSensitiveValueControl(string value)
    {
        var container = new Grid();
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var passwordBox = new PasswordBox
        {
            Password = value,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("CanvasBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 10
        };
        var visibleBox = CreateTextBox(value);
        visibleBox.Visibility = Visibility.Collapsed;
        var overlay = new Grid();
        overlay.Children.Add(passwordBox);
        overlay.Children.Add(visibleBox);
        var reveal = new CheckBox { Content = "显示", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 9 };
        reveal.Checked += (_, _) => { visibleBox.Text = passwordBox.Password; passwordBox.Visibility = Visibility.Collapsed; visibleBox.Visibility = Visibility.Visible; };
        reveal.Unchecked += (_, _) => { passwordBox.Password = visibleBox.Text; visibleBox.Visibility = Visibility.Collapsed; passwordBox.Visibility = Visibility.Visible; };
        Grid.SetColumn(reveal, 1);
        container.Children.Add(overlay);
        container.Children.Add(reveal);
        return new ValueControl(container, () => reveal.IsChecked == true ? visibleBox.Text : passwordBox.Password);
    }

    private TextBox CreateTextBox(string value) => new()
    {
        Text = value,
        MinWidth = 100,
        Margin = new Thickness(0, 0, 6, 0),
        Padding = new Thickness(6, 4, 6, 4),
        Background = (Brush)FindResource("CanvasBrush"),
        Foreground = (Brush)FindResource("TextBrush"),
        BorderBrush = (Brush)FindResource("LineBrush"),
        FontFamily = new FontFamily("Cascadia Code"),
        FontSize = 10
    };

    private ComboBox CreateComboBox(IReadOnlyList<string> options, string value)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = value,
            MinWidth = 100,
            Margin = new Thickness(0, 0, 6, 0),
            Background = (Brush)FindResource("CanvasBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 10
        };
        return combo;
    }

    private static void AddRemoveButton(Grid grid, Action remove)
    {
        var button = new Button
        {
            Content = "×", Width = 26, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brushes.IndianRed,
            BorderBrush = Brushes.Transparent, ToolTip = "移除此项"
        };
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
    }

    private void RefreshArgumentLabels()
    {
        for (var index = 0; index < _argumentRows.Count; index++)
            if (_argumentRows[index].Definition is null)
                _argumentRows[index].Label.Text = $"参数 {index + 1}";
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var arguments = new List<string>();
        foreach (var row in _argumentRows)
        {
            var value = row.Value.Read();
            if (!ValidateValue(row.Definition, value, out var error))
            {
                ShowInputError($"参数“{row.Definition?.Name ?? row.Label.Text}”{error}");
                row.Value.Root.Focus();
                return;
            }
            if (row.Definition is not null || !string.IsNullOrWhiteSpace(value))
                arguments.Add(value);
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _environmentRows)
        {
            var name = row.Definition?.Name ?? row.Name.Text.Trim();
            var value = row.Value.Read();
            if (name.Length == 0 && value.Length == 0)
                continue;
            if (!EnvironmentNamePattern.IsMatch(name))
            {
                ShowInputError($"环境变量名称无效：{name}。名称须以字母或下划线开头，只能包含字母、数字和下划线。");
                row.Name.Focus();
                return;
            }
            if (name.Equals("ONLYSHELL_ARG_COUNT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ONLYSHELL_ARG_", StringComparison.OrdinalIgnoreCase))
            {
                ShowInputError($"{name} 是 OnlyShell 为 CMD 参数保留的名称，请换一个变量名。");
                row.Name.Focus();
                return;
            }
            if (!ValidateValue(row.Definition, value, out var error))
            {
                ShowInputError($"环境变量“{name}”{error}");
                row.Value.Root.Focus();
                return;
            }
            if (!environment.TryAdd(name, value))
            {
                ShowInputError($"环境变量重复：{name}");
                row.Name.Focus();
                return;
            }
        }

        Inputs = new ScriptExecutionInputs(arguments, environment);
        DialogResult = true;
    }

    private static bool ValidateValue(ScriptInputFieldDefinition? definition, string value, out string error)
    {
        error = string.Empty;
        if (definition is null)
            return true;
        if (definition.IsRequired && string.IsNullOrWhiteSpace(value))
        {
            error = "为必填项，请输入值。";
            return false;
        }
        if (value.Length == 0)
            return true;
        switch (definition.ValueType)
        {
            case ScriptInputValueType.Number when !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                error = "必须是有效数字。";
                return false;
            case ScriptInputValueType.Boolean when !bool.TryParse(value, out _):
                error = "必须填写 true 或 false。";
                return false;
            case ScriptInputValueType.Choice:
                var options = definition.Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!options.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"必须是配置的选项之一：{string.Join(", ", options)}。";
                    return false;
                }
                break;
        }
        return true;
    }

    private void ShowInputError(string message) =>
        MessageBox.Show(this, message, "Script Inputs / Validation", MessageBoxButton.OK, MessageBoxImage.Warning);

    private sealed record ValueControl(FrameworkElement Root, Func<string> Read);
    private sealed record ArgumentRow(Grid Grid, TextBlock Label, ScriptInputFieldDefinition? Definition, ValueControl Value);
    private sealed record EnvironmentRow(Grid Grid, TextBox Name, ScriptInputFieldDefinition? Definition, ValueControl Value);
}
