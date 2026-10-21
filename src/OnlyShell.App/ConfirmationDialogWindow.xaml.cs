using System.Windows;

namespace OnlyShell.App;

public partial class ConfirmationDialogWindow : Window
{
    public ConfirmationDialogWindow(
        string eyebrow,
        string heading,
        string message,
        string confirmLabel,
        bool destructive,
        bool showCancel = true)
    {
        InitializeComponent();
        EyebrowText.Text = $"// {eyebrow}";
        HeadingText.Text = heading;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (destructive)
        {
            ConfirmButton.Background = (System.Windows.Media.Brush)FindResource("Brush.Status.Error");
            ConfirmButton.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.HighEmphasis");
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DialogHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }
}
