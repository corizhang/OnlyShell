using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
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
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) => _driveSelectionReady = true;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveSection) && _viewModel.ActiveSection == "终端")
            FocusActiveTerminal();
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
        var session = _viewModel.SelectedTerminalTab?.Session;
        var terminal = FindVisualChild<TerminalControl>(TerminalTabs);
        if (session is null || !session.IsConnected || terminal?.IsKeyboardFocusWithin != true)
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
