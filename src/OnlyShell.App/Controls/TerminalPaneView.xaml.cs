using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text;
using System.Text.RegularExpressions;
using OnlyShell.Presentation.ViewModels;

namespace OnlyShell.App.Controls;

public partial class TerminalPaneView : UserControl
{
    private int _matchCount;
    private int _currentMatch = -1;

    public TerminalPaneView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Terminal.SelectionStateChanged += Terminal_SelectionStateChanged;
        Terminal.TitleChanged += Terminal_TitleChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F)
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBar.Visibility == System.Windows.Visibility.Visible)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void OpenSearch()
    {
        SelectionToolbarPopup.IsOpen = false;
        SearchBar.Visibility = System.Windows.Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void TerminalContextFind_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void GitMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        GitMenuPopup.PlacementTarget = button;
        GitMenuPopup.IsOpen = true;
    }

    private void Terminal_TitleChanged(object? sender, string title)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Terminal_TitleChanged(sender, title));
            return;
        }

        if (DataContext is TerminalTabViewModel terminalTab)
            terminalTab.UpdateGitContextFromTerminalTitle(title);
    }

    private void GitCommand_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalTabViewModel { IsConnected: true, Session: { } session } terminalTab
            || sender is not FrameworkElement { Tag: string commandKey }
            || string.IsNullOrWhiteSpace(commandKey))
            return;

        var command = BuildGitCommand(commandKey, terminalTab);
        if (command is null)
            return;

        session.Write(session.InputEncoding.GetBytes(command + "\r"));
        GitMenuPopup.IsOpen = false;
        Terminal.Focus();
    }

    private string? BuildGitCommand(string key, TerminalTabViewModel terminalTab)
    {
        return key switch
        {
            "stage-all" => "git add -A :/",
            "unstage-all" => "git reset HEAD -- :/",
            "stash-save" => "git stash push -u",
            "stash-pop" => "git stash pop",
            "commit" => PromptForValue("提交", "提交说明", terminalTab.Kind == TerminalSessionKind.Local && terminalTab.LocalShellId == "cmd"
                ? ValidateCmdCommitMessage : null) is { } message
                ? $"git commit -m {QuoteForShell(message, terminalTab)}" : null,
            "branch-create" => PromptForValue("新建分支", "新分支名称", ValidateBranchName) is { } newBranch
                ? $"git switch -c {QuoteForShell(newBranch, terminalTab)}" : null,
            "branch-switch" => PromptForValue("切换分支", "目标分支名称", ValidateBranchName) is { } branch
                ? $"git switch {QuoteForShell(branch, terminalTab)}" : null,
            _ => key
        };
    }

    private string? PromptForValue(string title, string prompt, Func<string, string?>? validate = null)
    {
        var dialog = new GitInputWindow(title, prompt, validate: validate) { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private static string? ValidateBranchName(string branch)
    {
        if (!Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("//", StringComparison.Ordinal)
            || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || branch.EndsWith('/')
            || branch.EndsWith('.'))
            return "分支名仅支持字母、数字、点、下划线、斜杠和连字符，且不能以点或斜杠结尾。";
        return null;
    }

    private static string? ValidateCmdCommitMessage(string message) =>
        message.IndexOfAny(['%', '!', '"', '\r', '\n']) >= 0
            ? "CMD 的提交说明不能包含 %、!、双引号或换行符。"
            : null;

    private static string QuoteForShell(string value, TerminalTabViewModel terminalTab)
    {
        if (terminalTab.Kind == TerminalSessionKind.Local && terminalTab.LocalShellId is "pwsh" or "powershell")
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        if (terminalTab.Kind == TerminalSessionKind.Local && terminalTab.LocalShellId == "cmd")
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private void Terminal_SelectionStateChanged(object? sender, VirtualTerminal.TerminalSelectionStateEventArgs e)
    {
        if (!e.HasSelection || !e.Completed)
        {
            SelectionToolbarPopup.IsOpen = false;
            return;
        }

        var toolbarWidth = (double)FindResource("Control.Terminal.SelectionToolbar.Width");
        var toolbarHeight = (double)FindResource("Control.Terminal.SelectionToolbar.Height");
        SelectionToolbarPopup.HorizontalOffset = Math.Clamp(e.Anchor.X, 0, Math.Max(0, Terminal.ActualWidth - toolbarWidth));
        var toolbarOffset = (double)FindResource("Space.Terminal.SelectionToolbar.Offset");
        var preferredY = e.Anchor.Y >= toolbarHeight + toolbarOffset ? e.Anchor.Y - toolbarHeight - toolbarOffset : e.Anchor.Y + toolbarOffset;
        SelectionToolbarPopup.VerticalOffset = Math.Clamp(preferredY, 0, Math.Max(0, Terminal.ActualHeight - toolbarHeight));
        SelectionToolbarPopup.IsOpen = true;
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Terminal.CopySelectionToClipboard();
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.Write("TERMINAL", "Could not copy selected terminal text to the clipboard.", exception);
            MessageBox.Show(Window.GetWindow(this), "复制终端内容失败，请检查剪贴板是否被其他程序占用。", "OnlyShell / Clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SelectionToolbarPopup.IsOpen = false;
    }

    private async void PasteFromToolbar_Click(object sender, RoutedEventArgs e)
    {
        SelectionToolbarPopup.IsOpen = false;
        await Terminal.PasteFromClipboardAsync();
        Terminal.Focus();
    }

    private void QuickCommands_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalTabViewModel terminalTab)
            return;

        var window = new QuickCommandsWindow(terminalTab) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        Terminal.Focus();
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = System.Windows.Visibility.Collapsed;
        SearchBox.Clear();
        Terminal.ClearTextSearch();
        Terminal.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        Terminal.ClearTextSearch();
        _matchCount = Terminal.FindTextMatchCount(query);
        _currentMatch = _matchCount > 0 && Terminal.NavigateToTextMatch(query, 0) ? 0 : -1;
        UpdateSearchCount();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveToMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
    }

    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => MoveToMatch(-1);
    private void NextMatch_Click(object sender, RoutedEventArgs e) => MoveToMatch(1);
    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void MoveToMatch(int direction)
    {
        if (_matchCount == 0)
            return;

        _currentMatch = (_currentMatch + direction + _matchCount) % _matchCount;
        Terminal.NavigateToTextMatch(SearchBox.Text, _currentMatch);
        UpdateSearchCount();
    }

    private void UpdateSearchCount() => SearchResultCount.Text = _matchCount == 0
        ? "0 / 0"
        : $"{_currentMatch + 1} / {_matchCount}";
}
