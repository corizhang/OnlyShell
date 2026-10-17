using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OnlyShell.Core.Files;
using OnlyShell.Presentation.ViewModels;

namespace OnlyShell.App.Controls;

public partial class SftpWorkspaceView : UserControl
{
    private Point _dragStartPoint;
    private bool _driveSelectionReady;

    private WorkspaceViewModel ViewModel => (WorkspaceViewModel)DataContext;

    public SftpWorkspaceView()
    {
        InitializeComponent();
        Loaded += (_, _) => _driveSelectionReady = true;
    }

    private void RemoteFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.OpenRemoteDirectoryCommand.CanExecute(null))
            ViewModel.OpenRemoteDirectoryCommand.Execute(null);
    }

    private void RemoteFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
            ViewModel.SetSelectedRemoteFiles(list.SelectedItems.OfType<RemoteFileEntry>());
    }

    private void LocalFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
        {
            ViewModel.SetSelectedLocalFiles(list.SelectedItems.OfType<RemoteFileEntry>());
            ViewModel.SelectedLocalEntry = list.SelectedItem as RemoteFileEntry;
        }
    }

    private void LocalFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.OpenLocalDirectoryCommand.CanExecute(null))
            ViewModel.OpenLocalDirectoryCommand.Execute(null);
    }

    private void LocalDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_driveSelectionReady && sender is ComboBox { SelectedItem: string drive })
            ViewModel.SwitchLocalDrive(drive);
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStartPoint = e.GetPosition(null);

    private void LocalFiles_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListView list || list.SelectedItems.Count == 0)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entries = list.SelectedItems.OfType<RemoteFileEntry>().ToArray();
        DragDrop.DoDragDrop(list, new DataObject("OnlyShell.LocalSelection", entries), DragDropEffects.Copy);
    }

    private void RemoteFiles_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListView list || list.SelectedItems.Count == 0)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entries = list.SelectedItems.OfType<RemoteFileEntry>().ToArray();
        DragDrop.DoDragDrop(list, new DataObject("OnlyShell.RemoteSelection", entries), DragDropEffects.Copy);
    }

    private void FilePane_DragOver(object sender, DragEventArgs e)
    {
        var allowed = sender is ListView list && list.Name switch
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
        if (e.Data.GetData("OnlyShell.RemoteSelection") is RemoteFileEntry[] entries)
        {
            ViewModel.SetSelectedRemoteFiles(entries);
            ViewModel.DownloadRemoteFileCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RemoteFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("OnlyShell.LocalSelection") is RemoteFileEntry[] entries)
        {
            ViewModel.SetSelectedLocalFiles(entries);
            ViewModel.UploadFileCommand.Execute(null);
            e.Handled = true;
        }
    }
}
