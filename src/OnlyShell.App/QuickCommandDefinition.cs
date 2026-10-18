namespace OnlyShell.App;

public sealed record QuickCommandDefinition(
    Guid Id,
    string Name,
    string Category,
    string Command,
    string Description)
{
    public List<QuickCommandVariable> Variables { get; init; } = [];
}

public sealed class QuickCommandVariable
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public bool Required { get; set; }
    public bool Secret { get; set; }
    public bool RememberValue { get; set; } = true;
    public string LastValue { get; set; } = "";
}
