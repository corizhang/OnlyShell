using System.Windows;

namespace OnlyShell.App;

public partial class SftpInputWindow : Window
{
    public string? Value => DialogResult == true ? ValueInput.Text.Trim() : null;

    public SftpInputWindow(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueInput.Text = defaultValue;
        Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueInput.Text))
            return;
        DialogResult = true;
    }
}
