using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using OnlyShell.Core.Scripts;

namespace OnlyShell.Terminal;

public sealed class LocalScriptRunner : ILocalScriptRunner
{
    public async Task<RemoteScriptRunResult> RunAsync(
        ScriptRuntimeKind runtime,
        string content,
        string workingDirectory,
        ScriptExecutionInputs inputs,
        CancellationToken cancellationToken = default,
        Action<string, bool>? outputReceived = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"本机工作目录不存在：{workingDirectory}");

        string? temporaryScriptPath = null;
        if (runtime == ScriptRuntimeKind.PowerShell)
        {
            temporaryScriptPath = Path.Combine(Path.GetTempPath(), $"OnlyShell-{Guid.NewGuid():N}.ps1");
        }

        try
        {
        if (temporaryScriptPath is not null)
        {
            // Windows PowerShell 5.1 may use the active OEM code page for redirected
            // console output. The parent process decodes stdout/stderr as UTF-8, so
            // initialize the child host explicitly before running user code.
            const string utf8ConsoleSetup = "$utf8 = [System.Text.UTF8Encoding]::new($false)\r\n[Console]::OutputEncoding = $utf8\r\n$OutputEncoding = $utf8\r\n";
            await File.WriteAllTextAsync(temporaryScriptPath, utf8ConsoleSetup + content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        }
        var startInfo = CreateStartInfo(runtime, temporaryScriptPath);
        startInfo.WorkingDirectory = workingDirectory;
        foreach (var (name, value) in inputs.EnvironmentVariables)
            startInfo.Environment[name] = value;
        if (runtime == ScriptRuntimeKind.Cmd)
        {
            startInfo.Environment["ONLYSHELL_ARG_COUNT"] = inputs.Arguments.Count.ToString(CultureInfo.InvariantCulture);
            for (var index = 0; index < inputs.Arguments.Count; index++)
                startInfo.Environment[$"ONLYSHELL_ARG_{index + 1}"] = inputs.Arguments[index];
        }
        else if (runtime == ScriptRuntimeKind.PowerShell)
        {
            foreach (var argument in inputs.Arguments)
                startInfo.ArgumentList.Add(argument);
        }
        else if (runtime == ScriptRuntimeKind.Python3)
        {
            foreach (var argument in inputs.Arguments)
                startInfo.ArgumentList.Add(argument);
        }
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = runtime == ScriptRuntimeKind.Cmd
            ? GetWindowsOemEncoding()
            : new UTF8Encoding(false);
        // cmd.exe /U writes internal command output as UTF-16LE, regardless of the
        // machine's OEM code page. PowerShell and Python are explicitly UTF-8.
        startInfo.StandardOutputEncoding = runtime == ScriptRuntimeKind.Cmd
            ? Encoding.Unicode
            : new UTF8Encoding(false);
        startInfo.StandardErrorEncoding = runtime == ScriptRuntimeKind.Cmd
            ? Encoding.Unicode
            : new UTF8Encoding(false);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动本机脚本进程。");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"无法启动运行时“{runtime}”：{exception.Message}", exception);
        }

        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcessTree(process));
        var outputTask = ReadStreamAsync(process.StandardOutput, isError: false, outputReceived);
        var errorTask = ReadStreamAsync(process.StandardError, isError: true, outputReceived);
        if (runtime == ScriptRuntimeKind.PowerShell)
            process.StandardInput.Close();

        try
        {
            if (runtime != ScriptRuntimeKind.PowerShell)
            {
                var input = runtime == ScriptRuntimeKind.Cmd
                    ? $"set ONLYSHELL_ARG_COUNT={inputs.Arguments.Count}\r\n{content}\r\nexit\r\n"
                    : content;
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);
            return new RemoteScriptRunResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            await outputTask;
            await errorTask;
            throw;
        }
        }
        finally
        {
            if (temporaryScriptPath is not null)
            {
                try { File.Delete(temporaryScriptPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(ScriptRuntimeKind runtime, string? temporaryScriptPath)
    {
        var info = new ProcessStartInfo();
        switch (runtime)
        {
            case ScriptRuntimeKind.PowerShell:
                info.FileName = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe")
                    ?? throw new FileNotFoundException("未找到 PowerShell（pwsh.exe 或 powershell.exe）。");
                info.ArgumentList.Add("-NoLogo");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-File");
                info.ArgumentList.Add(temporaryScriptPath ?? throw new InvalidOperationException("PowerShell 临时脚本文件未创建。"));
                break;
            case ScriptRuntimeKind.Cmd:
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.ArgumentList.Add("/D");
                info.ArgumentList.Add("/Q");
                info.ArgumentList.Add("/U");
                break;
            case ScriptRuntimeKind.Python3:
                var launcher = FindOnPath("py.exe");
                if (launcher is not null)
                {
                    info.FileName = launcher;
                    info.ArgumentList.Add("-3");
                    info.ArgumentList.Add("-u");
                }
                else
                {
                    info.FileName = FindOnPath("python.exe")
                        ?? throw new FileNotFoundException("未找到 Python 3；请安装 Python 并加入 PATH，或安装 Python Launcher（py.exe）。");
                    info.ArgumentList.Add("-u");
                }
                info.ArgumentList.Add("-");
                info.Environment["PYTHONIOENCODING"] = "utf-8";
                info.Environment["PYTHONUNBUFFERED"] = "1";
                break;
            default:
                throw new NotSupportedException($"本机暂不支持运行时：{runtime}");
        }

        return info;
    }

    private static async Task<string> ReadStreamAsync(
        StreamReader reader,
        bool isError,
        Action<string, bool>? outputReceived)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            if (count == 0)
                return result.ToString();

            var chunk = new string(buffer, 0, count);
            result.Append(chunk);
            outputReceived?.Invoke(chunk, isError);
        }
    }

    private static Encoding GetWindowsOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        return Encoding.GetEncoding(oemCodePage);
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
