namespace OnlyShell.Core.Files;

public sealed record RemoteFileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime LastWriteTime, bool IsSymbolicLink = false)
{
    public string TypeLabel => IsSymbolicLink ? "LINK" : IsDirectory ? "DIR" : "FILE";
    public string SizeLabel => IsDirectory ? "—" : $"{Size:N0} bytes";
    public string IconGlyph => IsSymbolicLink ? "🔗" : IsDirectory ? "📁" : Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".py" => "🐍",
        ".ps1" or ".psm1" or ".psd1" => "⚡",
        ".sh" or ".bash" or ".zsh" => "⌨",
        ".cs" or ".cpp" or ".c" or ".h" or ".java" or ".js" or ".ts" or ".tsx" or ".jsx" or ".html" or ".css" or ".sql" => "‹›",
        ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".conf" => "⚙",
        ".md" or ".txt" or ".log" or ".csv" => "📄",
        ".pdf" => "📕",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "🖼",
        ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => "📦",
        ".mp3" or ".wav" or ".flac" or ".aac" => "🎵",
        ".mp4" or ".mkv" or ".mov" or ".avi" => "🎞",
        ".exe" or ".msi" or ".dll" => "🧩",
        _ => "📄"
    };
}
