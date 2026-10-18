using System.Windows;

namespace OnlyShell.App;

public partial class SyncPromptWindow : Window
{
    private readonly bool _isSecret;
    private readonly bool _confirmSecret;
    public string? Secret => SecretBox.Password;

    public SyncPromptWindow(string title, string message, bool isSecret, bool confirmSecret = false)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = message;
        _isSecret = isSecret;
        _confirmSecret = confirmSecret && isSecret;
        SecretBox.Visibility = isSecret ? Visibility.Visible : Visibility.Collapsed;
        ConfirmBox.Visibility = _confirmSecret ? Visibility.Visible : Visibility.Collapsed;
        if (_confirmSecret)
            ConfirmBox.ToolTip = "再次输入加密口令";
        if (!isSecret && confirmSecret)
        {
            AcceptButton.Content = "继续";
            AcceptButton.Background = (System.Windows.Media.Brush)FindResource("Brush.Status.Error");
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (_isSecret)
        {
            if (SecretBox.Password.Length < 12)
            {
                ValidationText.Text = "同步加密口令至少需要 12 个字符。";
                return;
            }
            if (_confirmSecret && !string.Equals(SecretBox.Password, ConfirmBox.Password, StringComparison.Ordinal))
            {
                ValidationText.Text = "两次输入的口令不一致。";
                return;
            }
        }
        DialogResult = true;
    }
}
