using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Xml;
using System.IO;
using Microsoft.Win32;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit;
using OnlyShell.Presentation.ViewModels;
using VirtualTerminal;

namespace OnlyShell.App;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<string, Color> _baseThemeColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _baseTypeSizes = new(StringComparer.Ordinal);
    private static readonly string[] ThemeBrushKeys =
    [
        "Brush.Canvas", "Brush.Surface", "Brush.Surface.Raised", "Brush.Surface.Input", "Brush.Surface.Editor",
        "Brush.Surface.Terminal", "Brush.Surface.Output", "Brush.Surface.Hover", "Brush.Surface.Selected",
        "Brush.Surface.SelectedHover", "Brush.Surface.SelectionQuiet", "Brush.Surface.HoverSubtle", "Brush.Surface.ListHover",
        "Brush.Surface.Disabled", "Brush.Surface.StatusBar", "Brush.Surface.Warning", "Brush.Surface.MetricTrack",
        "Brush.Surface.Metric", "Brush.Surface.TerminalToolbar", "Brush.Surface.TerminalPopup", "Brush.Surface.Error",
        "Brush.Surface.ListRow", "Brush.Surface.ListRowAlt", "Brush.Surface.EditorAlt", "Brush.Border.Subtle",
        "Brush.Border.Default", "Brush.Border.Strong", "Brush.Border.Focus", "Brush.Border.Selected",
        "Brush.Border.Disabled", "Brush.Border.Success", "Brush.Border.Warning", "Brush.Border.Dialog",
        "Brush.Border.Error", "Brush.Border.MetricsChartGrid", "Brush.Text.Primary", "Brush.Text.Secondary",
        "Brush.Text.Disabled", "Brush.Text.OnAccent", "Brush.Text.OnSelected", "Brush.Text.Header",
        "Brush.Text.Highlight", "Brush.Text.HighEmphasis", "Brush.Text.Tertiary", "Brush.Text.TerminalDefault",
        "Brush.Text.TerminalSuccess", "Brush.Text.Terminal", "Brush.Icon.Muted", "Brush.Accent.Primary",
        "Brush.Accent.Hover", "Brush.Status.Success", "Brush.Status.Info", "Brush.Status.Warning",
        "Brush.Status.Error", "Brush.Status.InfoBright", "Brush.Status.WarningBright", "Brush.Status.SuccessBright",
        "Brush.Accent.Purple"
    ];
    private static readonly string[] ScalableTypeSizeKeys =
    [
        "Type.Size.Caption", "Type.Size.Tiny", "Type.Size.MetricTiny", "Type.Size.Label", "Type.Size.Small",
        "Type.Size.Body", "Type.Size.BodyLarge", "Type.Size.BodyEmphasis", "Type.Size.Heading",
        "Type.Size.HeadingSmall", "Type.Size.Title", "Type.Size.SectionTitle", "Type.Size.Display", "Type.Size.PageTitle"
    ];

    public MainWindow(WorkspaceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        CaptureBaseDesignTokens();
        ApplyAppearancePreferences();
        DiagnosticLogPathText.Text = ApplicationDiagnostics.DirectoryPath;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ScriptCodeEditor.Text = _viewModel.ScriptContent;
        UpdateScriptEditorHighlighting();
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "诊断包包含应用日志。日志可能记录主机地址或异常上下文。请在分享前检查文件内容。是否继续导出？",
            "OnlyShell / Export Diagnostics",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出 OnlyShell 诊断包",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"OnlyShell-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ApplicationDiagnostics.Export(dialog.FileName);
            MessageBox.Show($"诊断包已导出：\n{dialog.FileName}", "OnlyShell / Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("DIAGNOSTICS", "Could not export diagnostic archive.", exception);
            MessageBox.Show($"导出诊断包失败：{exception.Message}", "OnlyShell / Diagnostics", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChooseScriptFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folders = _viewModel.Scripts.Select(script => script.FolderPath)
                .Where(path => !string.IsNullOrWhiteSpace(path));
            var picker = new FolderPickerWindow(folders, _viewModel.ScriptFolderPath) { Owner = this };
            if (picker.ShowDialog() == true)
                _viewModel.ScriptFolderPath = picker.SelectedPath;
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("FOLDER_PICKER", "Could not choose a script folder.", exception);
            MessageBox.Show(this, $"文件夹选择失败：{exception.Message}", "OnlyShell / Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewScript_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NewScriptCommand.Execute(null);
        ShowScriptInfoDialog();
    }

    private void EditScriptInfo_Click(object sender, RoutedEventArgs e) => ShowScriptInfoDialog();

    private void ShowScriptInfoDialog()
    {
        var dialog = new ScriptInfoWindow(
            _viewModel.ScriptName,
            _viewModel.ScriptDescription,
            _viewModel.ScriptFolderPath,
            _viewModel.SelectedScriptRuntime,
            _viewModel.SelectedScriptExecutionTarget,
            _viewModel.Scripts.Select(script => script.FolderPath))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _viewModel.ScriptName = dialog.ScriptName;
        _viewModel.ScriptDescription = dialog.ScriptDescription;
        _viewModel.ScriptFolderPath = dialog.ScriptFolderPath;
        _viewModel.SelectedScriptExecutionTarget = dialog.ExecutionTarget;
        _viewModel.SelectedScriptRuntime = dialog.ScriptRuntime;
    }

    private void RecentRuns_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox
            && ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: OnlyShell.Core.Scripts.ScriptRunLog run } item)
        {
            item.IsSelected = true;
            _viewModel.SelectedScriptRun = run;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveSection) && _viewModel.ActiveSection == "终端")
            FocusActiveTerminal();

        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedScriptRuntime))
            UpdateScriptEditorHighlighting();

        if (e.PropertyName is nameof(WorkspaceViewModel.AppearanceTheme)
            or nameof(WorkspaceViewModel.UiFontFamily) or nameof(WorkspaceViewModel.UiFontSize))
            ApplyAppearancePreferences();

        if (e.PropertyName == nameof(WorkspaceViewModel.ScriptContent)
            && ScriptCodeEditor.Text != _viewModel.ScriptContent)
            ScriptCodeEditor.Text = _viewModel.ScriptContent;
    }

    private void CaptureBaseDesignTokens()
    {
        foreach (var key in ThemeBrushKeys)
        {
            if (Application.Current.Resources[key] is SolidColorBrush brush)
                _baseThemeColors[key] = brush.Color;
        }

        foreach (var key in ScalableTypeSizeKeys)
        {
            if (Application.Current.Resources[key] is double size)
                _baseTypeSizes[key] = size;
        }
    }

    private void ApplyAppearancePreferences()
    {
        var colors = GetThemeOverrides(_viewModel.AppearanceTheme);
        foreach (var key in ThemeBrushKeys)
        {
            var color = colors.TryGetValue(key, out var hex)
                ? (Color)ColorConverter.ConvertFromString(hex)
                : _baseThemeColors.GetValueOrDefault(key);
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }

        var uiFont = new FontFamily(_viewModel.UiFontFamily);
        Application.Current.Resources["Font.UI"] = uiFont;
        Application.Current.Resources["Font.Window"] = uiFont;
        var sizeDelta = _viewModel.UiFontSize - 11;
        foreach (var key in ScalableTypeSizeKeys)
        {
            if (_baseTypeSizes.TryGetValue(key, out var baseSize))
                Application.Current.Resources[key] = Math.Clamp(baseSize + sizeDelta, 7, 30);
        }
    }

    private static IReadOnlyDictionary<string, string> GetThemeOverrides(string theme) => theme switch
    {
        "海洋蓝" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Brush.Canvas"] = "#101820", ["Brush.Surface"] = "#16222C", ["Brush.Surface.Raised"] = "#1D2D39",
            ["Brush.Surface.Input"] = "#0D151C", ["Brush.Surface.Editor"] = "#09121A", ["Brush.Surface.Terminal"] = "#050B10",
            ["Brush.Surface.Output"] = "#081510", ["Brush.Surface.Hover"] = "#263B49", ["Brush.Surface.Selected"] = "#29424B",
            ["Brush.Surface.SelectedHover"] = "#34535D", ["Brush.Surface.SelectionQuiet"] = "#20343D", ["Brush.Surface.HoverSubtle"] = "#1C2A34",
            ["Brush.Surface.ListHover"] = "#1B2B36", ["Brush.Surface.Disabled"] = "#18232B", ["Brush.Surface.StatusBar"] = "#131F28",
            ["Brush.Surface.Warning"] = "#292519", ["Brush.Surface.MetricTrack"] = "#263640", ["Brush.Surface.Metric"] = "#0B141B",
            ["Brush.Surface.TerminalToolbar"] = "#1B2A34", ["Brush.Surface.TerminalPopup"] = "#1D2B35", ["Brush.Surface.Error"] = "#291D20",
            ["Brush.Surface.ListRow"] = "#14202A", ["Brush.Surface.ListRowAlt"] = "#182630", ["Brush.Surface.EditorAlt"] = "#0B141C",
            ["Brush.Border.Subtle"] = "#30414D", ["Brush.Border.Default"] = "#405461", ["Brush.Border.Strong"] = "#526B79",
            ["Brush.Border.Focus"] = "#64C9D8", ["Brush.Border.Selected"] = "#4B7D86", ["Brush.Border.Disabled"] = "#293741",
            ["Brush.Border.Success"] = "#285344", ["Brush.Border.Warning"] = "#64552C", ["Brush.Border.Dialog"] = "#344752",
            ["Brush.Border.Error"] = "#8C4744", ["Brush.Border.MetricsChartGrid"] = "#293C47", ["Brush.Text.Primary"] = "#E1EAF0",
            ["Brush.Text.Secondary"] = "#A4B5C0", ["Brush.Text.Disabled"] = "#71828C", ["Brush.Text.OnAccent"] = "#0A1820",
            ["Brush.Text.OnSelected"] = "#E7F9FA", ["Brush.Text.Header"] = "#C6D5DF", ["Brush.Text.Highlight"] = "#FFFFFF",
            ["Brush.Text.HighEmphasis"] = "#EDF4F8", ["Brush.Text.Tertiary"] = "#81949F", ["Brush.Text.TerminalDefault"] = "#DCE6F2",
            ["Brush.Text.TerminalSuccess"] = "#8ED6A5", ["Brush.Text.Terminal"] = "#DDE7ED", ["Brush.Icon.Muted"] = "#A5B5BF",
            ["Brush.Accent.Primary"] = "#56C8CF", ["Brush.Accent.Hover"] = "#68D9DF", ["Brush.Status.Success"] = "#63C985",
            ["Brush.Status.Info"] = "#56C8CF", ["Brush.Status.Warning"] = "#E7B34D", ["Brush.Status.Error"] = "#E68174",
            ["Brush.Status.InfoBright"] = "#65D8EA", ["Brush.Status.WarningBright"] = "#F1C365", ["Brush.Status.SuccessBright"] = "#68E09B",
            ["Brush.Accent.Purple"] = "#B58CFF"
        },
        "高对比" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Brush.Canvas"] = "#080808", ["Brush.Surface"] = "#121212", ["Brush.Surface.Raised"] = "#1C1C1C",
            ["Brush.Surface.Input"] = "#050505", ["Brush.Surface.Editor"] = "#030303", ["Brush.Surface.Terminal"] = "#000000",
            ["Brush.Surface.Output"] = "#030805", ["Brush.Surface.Hover"] = "#303030", ["Brush.Surface.Selected"] = "#463500",
            ["Brush.Surface.SelectedHover"] = "#5B4600", ["Brush.Surface.SelectionQuiet"] = "#302500", ["Brush.Surface.HoverSubtle"] = "#202020",
            ["Brush.Surface.ListHover"] = "#262626", ["Brush.Surface.Disabled"] = "#161616", ["Brush.Surface.StatusBar"] = "#101010",
            ["Brush.Surface.Warning"] = "#211A00", ["Brush.Surface.MetricTrack"] = "#333333", ["Brush.Surface.Metric"] = "#050505",
            ["Brush.Surface.TerminalToolbar"] = "#1B1B1B", ["Brush.Surface.TerminalPopup"] = "#1B1B1B", ["Brush.Surface.Error"] = "#260D0D",
            ["Brush.Surface.ListRow"] = "#101010", ["Brush.Surface.ListRowAlt"] = "#181818", ["Brush.Surface.EditorAlt"] = "#030303",
            ["Brush.Border.Subtle"] = "#565656", ["Brush.Border.Default"] = "#777777", ["Brush.Border.Strong"] = "#A0A0A0",
            ["Brush.Border.Focus"] = "#5DEBFF", ["Brush.Border.Selected"] = "#E7A92F", ["Brush.Border.Disabled"] = "#454545",
            ["Brush.Border.Success"] = "#3E8B58", ["Brush.Border.Warning"] = "#94752A", ["Brush.Border.Dialog"] = "#777777",
            ["Brush.Border.Error"] = "#B14E4E", ["Brush.Border.MetricsChartGrid"] = "#444444", ["Brush.Text.Primary"] = "#FFFFFF",
            ["Brush.Text.Secondary"] = "#D0D0D0", ["Brush.Text.Disabled"] = "#A0A0A0", ["Brush.Text.OnAccent"] = "#080808",
            ["Brush.Text.OnSelected"] = "#FFF4D6", ["Brush.Text.Header"] = "#F0F0F0", ["Brush.Text.Highlight"] = "#FFFFFF",
            ["Brush.Text.HighEmphasis"] = "#FFFFFF", ["Brush.Text.Tertiary"] = "#B8B8B8", ["Brush.Text.TerminalDefault"] = "#FFFFFF",
            ["Brush.Text.TerminalSuccess"] = "#A4FFB8", ["Brush.Text.Terminal"] = "#FFFFFF", ["Brush.Icon.Muted"] = "#D0D0D0",
            ["Brush.Accent.Primary"] = "#FFC247", ["Brush.Accent.Hover"] = "#FFD36F", ["Brush.Status.Success"] = "#72F09B",
            ["Brush.Status.Info"] = "#63E9F5", ["Brush.Status.Warning"] = "#FFC247", ["Brush.Status.Error"] = "#FF9388",
            ["Brush.Status.InfoBright"] = "#75F2FF", ["Brush.Status.WarningBright"] = "#FFD36F", ["Brush.Status.SuccessBright"] = "#7AFFA8",
            ["Brush.Accent.Purple"] = "#D3B6FF"
        },
        _ => new Dictionary<string, string>(StringComparer.Ordinal)
    };

    private void ScriptCodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor && editor.Text != _viewModel.ScriptContent)
            _viewModel.ScriptContent = editor.Text;
    }

    private void TemplateSecretDefaultBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && passwordBox.DataContext is ScriptTemplateFieldViewModel field)
            passwordBox.Password = field.DefaultValue;
    }

    private void TemplateSecretDefaultBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && passwordBox.DataContext is ScriptTemplateFieldViewModel field
            && field.DefaultValue != passwordBox.Password)
            field.DefaultValue = passwordBox.Password;
    }

    private void UpdateScriptEditorHighlighting()
    {
        var name = _viewModel.SelectedScriptRuntime switch
        {
            "Python 3" => "OnlyShell Python",
            "PowerShell" => "OnlyShell PowerShell",
            "CMD" => "OnlyShell CMD",
            _ => "OnlyShell Bash"
        };
        ScriptCodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(name)
            ?? CreateScriptHighlighting(name);
    }

    private IHighlightingDefinition CreateScriptHighlighting(string name)
    {
        var python = name.EndsWith("Python", StringComparison.Ordinal);
        var cmd = name.EndsWith("CMD", StringComparison.Ordinal);
        var powershell = name.EndsWith("PowerShell", StringComparison.Ordinal);
        var keywords = python
            ? "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield print"
            : cmd
                ? "assoc break call cd chcp chdir cls color copy date del dir doskey echo endlocal erase exit for ftype goto if md mkdir mklink move path pause popd prompt pushd rd rem ren rename rmdir set setlocal shift shutdown sort start subst time title tree type ver verify vol"
                : powershell
                    ? "begin break catch class continue data define do dynamicparam else elseif end endfinally endfor endfunction endif endprocess endregion endtry endwhile enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow write-output write-host Write-Error Get-ChildItem Get-Content Set-Content Test-Path New-Item Remove-Item Get-Process Get-Service Start-Service Stop-Service"
                    : "if then else elif fi for while do done case esac function select time coproc in export local readonly declare typeset unset source alias bg bind break builtin caller cd command compgen complete continue dirs disown echo enable eval exec exit false fc fg getopts hash help history jobs kill let logout mapfile popd printf pushd pwd read readarray return set shift shopt suspend test times trap true type typeset ulimit umask unalias wait sudo apt systemctl journalctl grep awk sed find xargs chmod chown mkdir rm cp mv cat echo printf";
        var commentRule = cmd
            ? "<Rule color=\"Comment\">(?i)^\\s*(rem\\b|::).*$</Rule>"
            : "<Span color=\"Comment\" begin=\"#\" />";
        var variableRule = cmd
            ? "<Rule color=\"Variable\">%[^%]+%</Rule>"
            : (powershell || name.EndsWith("Bash", StringComparison.Ordinal))
                ? "<Rule color=\"Variable\">\\$[A-Za-z_][A-Za-z0-9_]*</Rule>"
                : string.Empty;
        var syntax = $"""
            <SyntaxDefinition name="{name}" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{GetSyntaxColor("Brush.Syntax.Comment")}" fontStyle="italic" />
              <Color name="String" foreground="{GetSyntaxColor("Brush.Syntax.String")}" />
              <Color name="Keyword" foreground="{GetSyntaxColor("Brush.Syntax.Keyword")}" />
              <Color name="Number" foreground="{GetSyntaxColor("Brush.Syntax.Number")}" />
              <Color name="Variable" foreground="{GetSyntaxColor("Brush.Syntax.Variable")}" />
              <RuleSet>
                {commentRule}
                <Span color="String" begin="&quot;" end="&quot;" />
                <Span color="String" begin="'" end="'" />
                {variableRule}
                <Keywords color="Keyword">{string.Join("", keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => $"<Word>{word}</Word>"))}</Keywords>
                <Rule color="Number">\b[0-9]+(\.[0-9]+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        using var reader = XmlReader.Create(new StringReader(syntax));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(name, [], definition);
        return definition;
    }

    private string GetSyntaxColor(string resourceKey)
    {
        var color = ((System.Windows.Media.SolidColorBrush)FindResource(resourceKey)).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void TerminalTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, TerminalTabs))
            FocusActiveTerminal();
    }

    private async void Hosts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: HostCatalogNode { Host: { } host } })
            await _viewModel.OpenHostTerminalCommand.ExecuteAsync(host);
    }

    private async void QuickConnectHosts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: OnlyShell.Core.Hosts.HostProfile host })
            await _viewModel.OpenHostTerminalCommand.ExecuteAsync(host);
    }

    private async void SplitQuickConnectHosts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: OnlyShell.Core.Hosts.HostProfile host, Tag: TerminalTabViewModel placeholder })
            await _viewModel.OpenHostInSplitTabCommand.ExecuteAsync(
                new WorkspaceViewModel.SplitHostConnectionRequest(host, placeholder));
    }

    private void SplitTabHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: TerminalTabViewModel { IsNewTab: false } tab } element)
            return;

        DragDrop.DoDragDrop(element, tab, DragDropEffects.Move);
    }

    private void SplitTab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TerminalTabViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SplitTab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not TabControl { Tag: string pane } || e.Data.GetData(typeof(TerminalTabViewModel)) is not TerminalTabViewModel tab)
            return;

        _viewModel.MoveSplitTabCommand.Execute(new WorkspaceViewModel.SplitTabMoveRequest(tab, pane == "left"));
        e.Handled = true;
    }

    private void FocusActiveTerminal() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var terminal = FindVisualChild<TerminalControl>(TerminalTabs);
        if (terminal is not null)
        {
            terminal.Focus();
            Keyboard.Focus(terminal);
        }
    });

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var tab = _viewModel.SelectedTerminalTab;
        var session = tab?.Session;
        var terminal = FindVisualChild<TerminalControl>(TerminalTabs);
        if (session is null || tab?.IsConnected != true || terminal?.IsKeyboardFocusWithin != true)
            return;

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.V || (key == Key.C && (modifiers & ModifierKeys.Shift) != 0))
            return;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is < 0x41 or > 0x5A)
            return;

        session.Write(new byte[] { (byte)(virtualKey - 0x40) });
        e.Handled = true;
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.CloseAllTerminalsAsync();
        base.OnClosed(e);
    }

    private static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
            return null;
        if (root is T target)
            return target;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = FindVisualChild<T>(VisualTreeHelper.GetChild(root, index));
            if (child is not null)
                return child;
        }
        return null;
    }
}
