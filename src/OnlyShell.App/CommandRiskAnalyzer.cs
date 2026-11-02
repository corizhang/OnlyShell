using System.Text.RegularExpressions;

namespace OnlyShell.App;

public enum CommandRiskLevel
{
    Low,
    Review,
    High
}

public sealed record CommandRiskAssessment(CommandRiskLevel Level, IReadOnlyList<string> Reasons)
{
    public string Summary => Level switch
    {
        CommandRiskLevel.High => "高风险",
        CommandRiskLevel.Review => "需要复核",
        _ => "未发现明显危险模式"
    };

    public string Detail => Reasons.Count == 0
        ? "本地规则未发现明显危险模式（启发式检查，不代表命令安全）。"
        : $"{Summary}：{string.Join("；", Reasons)}（本地启发式检查，请自行确认命令影响。）";
}

public static class CommandRiskAnalyzer
{
    private static readonly (string Pattern, string Reason)[] HighRiskPatterns =
    [
        (@"\brm\s+(-\S*rf\S*|-\S*fr\S*|--recursive\b.*--force\b)", "递归强制删除文件"),
        (@"\bmkfs(?:\.\w+)?\b", "格式化文件系统"),
        (@"\bdd\b.*\bof=", "直接写入磁盘或设备"),
        (@"\bshred\b", "不可逆覆盖文件"),
        (@"\b(?:format(?:\.com)?\s+[a-z]:|diskpart\b)", "格式化或分区磁盘"),
        (@"\bRemove-Item\b.*-(?:Recurse|Force)", "PowerShell 强制递归删除"),
        (@"\bgit\s+reset\s+--hard\b", "丢弃 Git 工作区改动"),
        (@"\bgit\s+clean\s+-\S*f\S*", "删除 Git 未跟踪文件"),
        (@"\b(?:DROP\s+(?:DATABASE|TABLE)|TRUNCATE\s+TABLE)\b", "删除数据库或清空数据表"),
        (@"\b(?:shutdown|reboot|Stop-Computer)\b", "关闭或重启系统")
    ];

    private static readonly (string Pattern, string Reason)[] ReviewRiskPatterns =
    [
        (@"\b(?:curl|wget|Invoke-WebRequest|iwr)\b[^\r\n]*\|\s*(?:sh|bash|zsh|iex|Invoke-Expression)\b", "下载后直接交给 shell 执行"),
        (@"\b(?:sudo|doas|runas)\b|\bStart-Process\b[^\r\n]*-Verb\s+RunAs", "使用管理员或提权权限"),
        (@"\b(?:chmod|chown)\s+-\S*R\b", "递归修改文件权限或所有者"),
        (@"\b(?:eval|Invoke-Expression|iex)\b|\$\([^)]*\)|`[^`]+`", "动态执行拼接或替换的命令"),
        (@"&&|\|\||;|(?<!\|)\|(?!\|)", "包含多步命令链或管道")
    ];

    public static CommandRiskAssessment Analyze(string command)
    {
        var high = MatchReasons(command, HighRiskPatterns);
        if (high.Count > 0)
            return new(CommandRiskLevel.High, high);
        var review = MatchReasons(command, ReviewRiskPatterns);
        return review.Count > 0
            ? new(CommandRiskLevel.Review, review)
            : new(CommandRiskLevel.Low, []);
    }

    private static List<string> MatchReasons(string command, (string Pattern, string Reason)[] patterns)
    {
        var reasons = new List<string>();
        foreach (var (pattern, reason) in patterns)
        {
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && !reasons.Contains(reason, StringComparer.Ordinal))
                reasons.Add(reason);
        }
        return reasons;
    }
}
