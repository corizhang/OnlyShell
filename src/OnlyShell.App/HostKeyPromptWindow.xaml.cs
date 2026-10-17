using System.Windows;
using OnlyShell.Core.Hosts;

namespace OnlyShell.App;

public partial class HostKeyPromptWindow : Window
{
    public HostKeyPromptWindow(HostProfile host, string currentFingerprint, string? previousFingerprint)
    {
        InitializeComponent();
        Target.Text = $"{host.UserName}@{host.HostName}:{host.Port}";
        CurrentFingerprint.Text = currentFingerprint;

        if (!string.IsNullOrWhiteSpace(previousFingerprint))
        {
            Heading.Text = "// SSH / HOST_KEY_CHANGED";
            TitleText.Text = "服务器密钥已变化";
            PreviousLabel.Visibility = Visibility.Visible;
            PreviousPanel.Visibility = Visibility.Visible;
            PreviousFingerprint.Text = previousFingerprint;
            Warning.Text = "密钥变化可能代表服务器重装，也可能表示网络攻击。请在可信渠道核验新指纹后再更新信任记录。";
            TrustButton.Content = "核验后更新并继续";
        }
    }

    private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
