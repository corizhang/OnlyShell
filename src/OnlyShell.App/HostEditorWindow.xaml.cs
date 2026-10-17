using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OnlyShell.Core.Hosts;

namespace OnlyShell.App;

public partial class HostEditorWindow : Window
{
    private readonly HostProfile? _current;

    public HostProfile? Result { get; private set; }

    public HostEditorWindow(HostProfile? current)
    {
        InitializeComponent();
        _current = current;

        if (current is null)
        {
            PortBox.Text = "22";
            GroupBox.Text = "未分组";
            return;
        }

        HeadingText.Text = "// EDIT_HOST";
        NameBox.Text = current.Name;
        HostBox.Text = current.HostName;
        PortBox.Text = current.Port.ToString();
        UserBox.Text = current.UserName;
        GroupBox.Text = current.Group ?? string.Empty;
        NoteBox.Text = current.Note ?? string.Empty;
        AuthenticationBox.SelectedIndex = current.AuthenticationMethod == SshAuthenticationMethod.PrivateKey ? 1 : 0;
        PrivateKeyPathBox.Text = current.PrivateKeyPath ?? string.Empty;
        UpdatePrivateKeyVisibility();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)
            || string.IsNullOrWhiteSpace(HostBox.Text)
            || string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ValidationText.Text = "显示名称、主机地址和用户名为必填项。";
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ValidationText.Text = "端口必须是 1 到 65535 之间的整数。";
            return;
        }

        var authenticationMethod = AuthenticationBox.SelectedIndex == 1
            ? SshAuthenticationMethod.PrivateKey
            : SshAuthenticationMethod.Password;
        var privateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text) ? null : PrivateKeyPathBox.Text.Trim();
        if (authenticationMethod == SshAuthenticationMethod.PrivateKey
            && (privateKeyPath is null || !File.Exists(privateKeyPath)))
        {
            ValidationText.Text = "请选择存在的私钥文件。";
            return;
        }

        Result = new HostProfile(
            _current?.Id ?? Guid.NewGuid(),
            NameBox.Text.Trim(),
            HostBox.Text.Trim(),
            port,
            UserBox.Text.Trim(),
            string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim(),
            _current?.CredentialReference,
            _current?.LastConnectedAt,
            _current?.IsFavorite ?? false,
            string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim(),
            authenticationMethod,
            privateKeyPath);
        DialogResult = true;
    }

    private void AuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePrivateKeyVisibility();

    private void UpdatePrivateKeyVisibility()
    {
        var isPrivateKey = AuthenticationBox?.SelectedIndex == 1;
        if (PrivateKeyPanel is null || PrivateKeyLabel is null)
            return;
        PrivateKeyPanel.Visibility = isPrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyLabel.Visibility = isPrivateKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥文件",
            Filter = "SSH 私钥|*|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = PrivateKeyPathBox.Text
        };
        if (dialog.ShowDialog(this) == true)
            PrivateKeyPathBox.Text = dialog.FileName;
    }
}
