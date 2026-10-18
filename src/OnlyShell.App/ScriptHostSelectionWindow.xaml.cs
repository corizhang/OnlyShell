using System.Collections.ObjectModel;
using System.Windows;
using OnlyShell.Core.Hosts;

namespace OnlyShell.App;

public partial class ScriptHostSelectionWindow : Window
{
    private readonly ObservableCollection<HostSelectionItem> _items;

    public ScriptHostSelectionWindow(IReadOnlyList<HostProfile> hosts, Guid? initiallySelectedHostId)
    {
        InitializeComponent();
        _items = new ObservableCollection<HostSelectionItem>(hosts.Select(host => new HostSelectionItem(
            host, host.Id == initiallySelectedHostId, $"{host.UserName}@{host.HostName}:{host.Port}")));
        HostsPanel.ItemsSource = _items;
        SelectionCount.DataContext = _items;
        UpdateCount();
    }

    public IReadOnlyList<HostProfile>? SelectedHosts { get; private set; }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedHosts = _items.Where(item => item.IsSelected).Select(item => item.Host).ToArray();
        if (SelectedHosts.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一台主机。", "批量执行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SelectedHosts.Count > 1 && MessageBox.Show(this,
                $"即将在 {SelectedHosts.Count} 台主机上依次执行当前脚本。请确认脚本内容可信且适用于所有目标。继续吗？",
                "确认批量执行", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }

    private void UpdateCount() => SelectionCount.Text = $"已选择 {_items.Count(item => item.IsSelected)} 台主机";

    private void HostSelectionChanged(object sender, RoutedEventArgs e) => UpdateCount();

    private sealed class HostSelectionItem(HostProfile host, bool isSelected, string endpoint)
    {
        public HostProfile Host { get; } = host;
        public bool IsSelected { get; set; } = isSelected;
        public string Endpoint { get; } = endpoint;
    }
}
