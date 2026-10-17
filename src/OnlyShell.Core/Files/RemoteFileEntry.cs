namespace OnlyShell.Core.Files;

public sealed record RemoteFileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime LastWriteTime, bool IsSymbolicLink = false)
{
    public string TypeLabel => IsSymbolicLink ? "LINK" : IsDirectory ? "DIR" : "FILE";
    public string SizeLabel => IsDirectory ? "—" : $"{Size:N0} bytes";
}
