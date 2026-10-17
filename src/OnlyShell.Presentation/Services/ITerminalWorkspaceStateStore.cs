namespace OnlyShell.Presentation.Services;

public sealed record RestorableTerminalTab(string Kind, Guid? HostId = null, string? Shell = null, string? WorkingDirectory = null);

public sealed record TerminalWorkspaceState(
    int SchemaVersion,
    IReadOnlyList<RestorableTerminalTab> Tabs,
    int SelectedTabIndex,
    string ActiveSection);

public interface ITerminalWorkspaceStateStore
{
    Task<TerminalWorkspaceState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TerminalWorkspaceState state, CancellationToken cancellationToken = default);
}
