using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyShell.Core.Scripts;

namespace OnlyShell.Infrastructure.Secrets;

/// <summary>Persists per-script runtime inputs encrypted for the current Windows user.</summary>
public sealed class WindowsProtectedScriptInputsStore : IScriptExecutionInputsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnlyShell", "script-inputs");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ScriptExecutionInputs?> LoadAsync(Guid scriptId, CancellationToken cancellationToken = default)
        => await LoadFromPathAsync(GetPath(scriptId), cancellationToken);

    public async Task<ScriptExecutionInputs?> LoadDefaultsAsync(Guid scriptId, CancellationToken cancellationToken = default)
        => await LoadFromPathAsync(GetDefaultsPath(scriptId), cancellationToken);

    private static async Task<ScriptExecutionInputs?> LoadFromPathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return null;

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        var clear = Unprotect(encrypted);
        try
        {
            return JsonSerializer.Deserialize<ScriptExecutionInputs>(clear, JsonOptions)
                ?? throw new InvalidDataException("Saved script inputs are empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Saved script inputs could not be parsed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task SaveAsync(Guid scriptId, ScriptExecutionInputs inputs, CancellationToken cancellationToken = default)
        => await SaveToPathAsync(GetPath(scriptId), inputs, cancellationToken);

    public async Task SaveDefaultsAsync(Guid scriptId, ScriptExecutionInputs inputs, CancellationToken cancellationToken = default)
        => await SaveToPathAsync(GetDefaultsPath(scriptId), inputs, cancellationToken);

    private static async Task SaveToPathAsync(string path, ScriptExecutionInputs inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(inputs, JsonOptions);
        byte[] encrypted;
        try { encrypted = Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }

        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task DeleteAsync(Guid scriptId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(scriptId);
        if (File.Exists(path))
            File.Delete(path);
        var defaultsPath = GetDefaultsPath(scriptId);
        if (File.Exists(defaultsPath))
            File.Delete(defaultsPath);
        return Task.CompletedTask;
    }

    private static string GetPath(Guid scriptId) => Path.Combine(DirectoryPath, $"{scriptId:N}.bin");
    private static string GetDefaultsPath(Guid scriptId) => Path.Combine(DirectoryPath, $"{scriptId:N}.defaults.bin");

    private static byte[] Protect(byte[] clear)
    {
        var input = Blob.FromBytes(clear);
        try
        {
            if (!CryptProtectData(ref input, "OnlyShell script execution inputs", 0, 0, 0, 0, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return output.ToBytesAndFree();
        }
        finally { input.Free(); }
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        var input = Blob.FromBytes(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input, 0, 0, 0, 0, 0, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return output.ToBytesAndFree();
        }
        finally { input.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public nint Data;

        public static Blob FromBytes(byte[] bytes)
        {
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new Blob { Length = bytes.Length, Data = pointer };
        }

        public byte[] ToBytesAndFree()
        {
            var bytes = new byte[Length];
            try { Marshal.Copy(Data, bytes, 0, Length); }
            finally { LocalFree(Data); Data = 0; }
            return bytes;
        }

        public void Free()
        {
            if (Data == 0)
                return;
            Marshal.Copy(new byte[Length], 0, Data, Length);
            Marshal.FreeHGlobal(Data);
            Data = 0;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, nint optionalEntropy, nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint optionalEntropy, nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern nint LocalFree(nint memory);
}
