using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OnlyShell.App;

public partial class GitInputWindow : Window
{
    private readonly Func<string, string?> _validate;

    public string Value => ValueBox.Text.Trim();

    public GitInputWindow(string title, string prompt, string initialValue = "", Func<string, string?>? validate = null)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = $"// GIT / {title.ToUpperInvariant()}";
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        _validate = validate ?? (_ => null);
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var value = Value;
        var validationError = string.IsNullOrWhiteSpace(value) ? "内容不能为空。" : _validate(value);
        if (validationError is not null)
        {
            ValidationText.Text = validationError;
            ValueBox.Focus();
            ValueBox.SelectAll();
            return;
        }

        DialogResult = true;
    }
}
