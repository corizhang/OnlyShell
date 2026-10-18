using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Terminal.CopySelectionToClipboard();
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
