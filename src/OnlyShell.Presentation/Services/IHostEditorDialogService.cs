using OnlyShell.Core.Hosts;

namespace OnlyShell.Presentation.Services;

public interface IHostEditorDialogService
{
    Task<HostProfile?> EditAsync(HostProfile? current);
    Task<bool> ConfirmDeleteAsync(HostProfile profile);
    Task ShowErrorAsync(string message);
}
