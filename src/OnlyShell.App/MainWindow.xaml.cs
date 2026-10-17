using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Xml;
using System.IO;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit;
using OnlyShell.Presentation.ViewModels;
using VirtualTerminal;

namespace OnlyShell.App;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private bool _driveSelectionReady;
    private readonly WorkspaceViewModel _viewModel;

    public MainWindow(WorkspaceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ScriptCodeEditor.Text = _viewModel.ScriptContent;
        UpdateScriptEditorHighlighting();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) => _driveSelectionReady = true;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveSection) && _viewModel.ActiveSection == "终端")
            FocusActiveTerminal();

        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedScriptRuntime))
            UpdateScriptEditorHighlighting();

        if (e.PropertyName == nameof(WorkspaceViewModel.ScriptContent)
            && ScriptCodeEditor.Text != _viewModel.ScriptContent)
            ScriptCodeEditor.Text = _viewModel.ScriptContent;
    }

    private void ScriptCodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor && editor.Text != _viewModel.ScriptContent)
            _viewModel.ScriptContent = editor.Text;
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

    private static IHighlightingDefinition CreateScriptHighlighting(string name)
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
              <Color name="Comment" foreground="#6A9955" fontStyle="italic" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Keyword" foreground="#C586C0" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Variable" foreground="#9CDCFE" />
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

    private void TerminalTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, TerminalTabs))
            FocusActiveTerminal();
    }

    private void RemoteFiles_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_viewModel.OpenRemoteDirectoryCommand.CanExecute(null))
            _viewModel.OpenRemoteDirectoryCommand.Execute(null);
    }

    private void RemoteFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
            _viewModel.SetSelectedRemoteFiles(list.SelectedItems.OfType<OnlyShell.Core.Files.RemoteFileEntry>());
    }

    private void LocalFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
        {
            _viewModel.SetSelectedLocalFiles(list.SelectedItems.OfType<OnlyShell.Core.Files.RemoteFileEntry>());
            _viewModel.SelectedLocalEntry = list.SelectedItem as OnlyShell.Core.Files.RemoteFileEntry;
        }
    }

    private void LocalFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenLocalDirectoryCommand.CanExecute(null))
            _viewModel.OpenLocalDirectoryCommand.Execute(null);
    }

    private void LocalDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_driveSelectionReady && sender is ComboBox { SelectedItem: string drive })
            _viewModel.SwitchLocalDrive(drive);
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStartPoint = e.GetPosition(null);

    private void LocalFiles_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListView list
            || list.SelectedItems.Count == 0)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entries = list.SelectedItems.OfType<OnlyShell.Core.Files.RemoteFileEntry>().ToArray();
        var data = new DataObject("OnlyShell.LocalSelection", entries);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
    }

    private void RemoteFiles_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListView list
            || list.SelectedItems.Count == 0)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entries = list.SelectedItems.OfType<OnlyShell.Core.Files.RemoteFileEntry>().ToArray();
        var data = new DataObject("OnlyShell.RemoteSelection", entries);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
    }

    private void FilePane_DragOver(object sender, DragEventArgs e)
    {
        var list = sender as ListView;
        var allowed = list?.Name switch
        {
            "LocalFileList" => e.Data.GetDataPresent("OnlyShell.RemoteSelection"),
            "RemoteFileList" => e.Data.GetDataPresent("OnlyShell.LocalSelection"),
            _ => false
        };
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LocalFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("OnlyShell.RemoteSelection") is OnlyShell.Core.Files.RemoteFileEntry[] entries)
        {
            _viewModel.SetSelectedRemoteFiles(entries);
            _viewModel.DownloadRemoteFileCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RemoteFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("OnlyShell.LocalSelection") is OnlyShell.Core.Files.RemoteFileEntry[] entries)
        {
            _viewModel.SetSelectedLocalFiles(entries);
            _viewModel.UploadFileCommand.Execute(null);
            e.Handled = true;
        }
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
