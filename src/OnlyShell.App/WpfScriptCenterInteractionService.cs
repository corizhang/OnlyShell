using System.Windows;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Scripts;

namespace OnlyShell.App;

public sealed class WpfScriptCenterInteractionService : IScriptCenterInteractionService
{
    public Task<IReadOnlyList<HostProfile>?> SelectHostsForBatchAsync(IReadOnlyList<HostProfile> hosts, Guid? initiallySelectedHostId)
    {
        var window = new ScriptHostSelectionWindow(hosts, initiallySelectedHostId) { Owner = Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true ? window.SelectedHosts : null);
    }

    public Task<bool> ConfirmDeleteAsync(ScriptDefinition script)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"确定删除脚本“{script.Name}”吗？该操作不会删除已有运行记录。",
            "删除脚本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task<bool> ConfirmLocalExecutionAsync()
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            "本机脚本会以当前 Windows 用户权限运行，可访问该用户有权限访问的文件和资源。请仅运行你信任的脚本。是否继续？",
            "确认本机脚本执行",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task<ScriptExecutionInputs?> PromptForExecutionInputsAsync(
        ScriptExecutionInputs initialInputs,
        IReadOnlyList<ScriptInputFieldDefinition> fields)
    {
        var window = new ScriptRunInputsWindow(initialInputs, fields) { Owner = Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true ? window.Inputs : null);
    }
}
