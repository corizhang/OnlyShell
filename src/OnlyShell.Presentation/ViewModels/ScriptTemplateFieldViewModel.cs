using CommunityToolkit.Mvvm.ComponentModel;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class ScriptTemplateFieldViewModel(
    string kind,
    string name = "",
    string valueType = "文本",
    bool isRequired = false,
    bool isSensitive = false,
    string options = "",
    string defaultValue = "",
    string group = "") : ObservableObject
{
    [ObservableProperty]
    private string kind = kind;

    [ObservableProperty]
    private string name = name;

    [ObservableProperty]
    private string valueType = valueType;

    [ObservableProperty]
    private bool isRequired = isRequired;

    [ObservableProperty]
    private bool isSensitive = isSensitive;

    [ObservableProperty]
    private string options = options;

    [ObservableProperty]
    private string defaultValue = defaultValue;

    [ObservableProperty]
    private string group = group;

    partial void OnValueTypeChanged(string value)
    {
        if (value == "密码")
            IsSensitive = true;
    }
}
