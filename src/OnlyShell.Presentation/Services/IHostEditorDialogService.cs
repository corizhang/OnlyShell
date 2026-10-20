using OnlyShell.Core.Hosts;

namespace OnlyShell.Presentation.Services;

public interface IHostEditorDialogService
{
    Task<HostProfile?> EditAsync(HostProfile? current, IReadOnlyList<string>? existingGroups = null);
    Task<bool> ConfirmDeleteAsync(HostProfile profile);
    Task ShowErrorAsync(string message);
}
