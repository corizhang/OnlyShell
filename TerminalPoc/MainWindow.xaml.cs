using System.Windows;
using System.Windows.Input;
using VirtualTerminal;

namespace TerminalPoc;

public partial class MainWindow : Window
{
    private SecureShellSession? _session;

    public MainWindow() => InitializeComponent();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            StatusText.Text = "Enter a valid host, port, and username.";
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Connecting…";
        bool hasExistingTerminal = _session is not null && ReferenceEquals(Terminal.Session, _session);
        try
        {
            if (_session is null)
            {
                _session = new SecureShellSession(HostBox.Text.Trim(), port, UsernameBox.Text.Trim(), PasswordBox.Password);
                _session.Client!.HostKeyReceived += (_, key) =>
                {
                    var decision = Dispatcher.Invoke(() => MessageBox.Show(
                        this,
                        $"SSH server fingerprint (SHA256):\nSHA256:{key.FingerPrintSHA256}\n\nCompare this with a fingerprint obtained through a trusted channel before continuing.",
                        "Verify SSH host key",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning));
                    key.CanTrust = decision == MessageBoxResult.Yes;
                };
            }

            await _session.ConnectAsync();
            Terminal.Session = _session;
            Terminal.AllowDirectInput = true;
            Terminal.Focus();
            Keyboard.Focus(Terminal);
            StatusText.Text = $"Connected to {HostBox.Text.Trim()}";
            DisconnectButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Terminal.AllowDirectInput = false;
            if (!hasExistingTerminal)
            {
                _session?.Dispose();
                _session = null;
            }
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = _session?.IsConnected == true;
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // VirtualTerminal encodes navigation keys, but currently leaves printable
        // Ctrl+letter combinations (including nano's Ctrl+X) to the text-input path.
        // WPF doesn't raise TextInput for these chords, so send the ASCII control byte.
        if (_session is null || !_session.IsConnected || !Terminal.AllowDirectInput || !Terminal.IsKeyboardFocusWithin)
            return;

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.V || (key == Key.C && (modifiers & ModifierKeys.Shift) != 0))
            return; // Preserve the terminal control's paste and copy shortcuts.

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is < 0x41 or > 0x5A)
            return;

        _session.Write(new byte[] { (byte)(virtualKey - 0x40) });
        e.Handled = true;
    }

    private async Task DisconnectAsync()
    {
        var session = _session;
        if (session is not null)
        {
            try { await session.DisconnectAsync(); }
            catch { /* The transport may already be gone. */ }
        }
        Terminal.AllowDirectInput = false;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        StatusText.Text = "Disconnected";
    }

    protected override async void OnClosed(EventArgs e)
    {
        await DisconnectAsync();
        Terminal.Session = null;
        _session?.Dispose();
        _session = null;
        base.OnClosed(e);
    }
}
