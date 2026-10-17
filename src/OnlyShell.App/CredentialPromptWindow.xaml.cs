using System.Windows;
using OnlyShell.Core.Hosts;

namespace OnlyShell.App;

public partial class CredentialPromptWindow : Window
{
    public string? Password => DialogResult == true ? PasswordInput.Password : null;
    public bool RememberOnThisDevice => RememberPassword.IsChecked == true;

    public CredentialPromptWindow(HostProfile host, string? savedPassword)
    {
        InitializeComponent();
        HostText.Text = $"{host.UserName}@{host.HostName}:{host.Port}";
        var isPrivateKey = host.AuthenticationMethod == SshAuthenticationMethod.PrivateKey;
        if (isPrivateKey)
        {
            Title = "SSH / KEY PASSPHRASE";
            AuthHeading.Text = "// AUTHENTICATION / PRIVATE_KEY";
            PromptTitle.Text = "输入私钥口令";
            SecurityNote.Text = "仅用于本次连接；私钥口令不会保存或同步。";
            RememberPassword.Visibility = Visibility.Collapsed;
        }
        else if (savedPassword is not null)
        {
            PasswordInput.Password = savedPassword;
            RememberPassword.IsChecked = true;
        }
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
