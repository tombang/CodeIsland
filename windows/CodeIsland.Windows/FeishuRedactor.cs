using System.Text.RegularExpressions;

namespace CodeIsland.Windows;

/// <summary>
/// Removes credential-shaped text before a command or task result is sent to Feishu.
/// Exact values passed to <see cref="Remember"/> are never written back out.
/// </summary>
public static class FeishuRedactor
{
    private static readonly List<string> Secrets = [];
    private static readonly Regex[] Patterns =
    [
        new(@"(?i)\b([a-z][a-z0-9+.-]*://)[^\s/@:""']+:[^\s/@""']+@", RegexOptions.Compiled),
        new(@"(?i)(authorization\s*[:=]\s*(?:(?:bearer|basic|token)\s+)?)([""']?)[^""'\s]+", RegexOptions.Compiled),
        new(@"(?i)((?:api[-_]?key|token|secret|password|passwd)\s*[:=]\s*)([""']?)[^""'\s]+", RegexOptions.Compiled),
        new(@"(?i)([""'][^""']*(?:api[-_]?key|token|secret|password)[""']\s*:\s*[""'])[^""']*([""'])", RegexOptions.Compiled),
        new(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|(?:AKIA|ASIA)[A-Z0-9]{16})\b", RegexOptions.Compiled),
        new(@"\b(?:glpat-[A-Za-z0-9_-]{20,}|xox[abposre]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|hf_[A-Za-z0-9]{30,})\b", RegexOptions.Compiled),
        new(@"(?i)((?:api[-_]?key|apikey|token|secret|password|passwd)=)([""']?)[^\s""'&]+", RegexOptions.Compiled),
        new(@"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"/(?:Users|home)/[^/\s]+", RegexOptions.Compiled)
    ];
    private static readonly string[] Replacements =
    [
        "$1[REDACTED]@",
        "$1[REDACTED]",
        "$1[REDACTED]",
        "$1[REDACTED]$2",
        "[REDACTED]",
        "[REDACTED]",
        "$1$2[REDACTED]",
        "~",
        "~"
    ];

    public static void Remember(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 8) return;
        lock (Secrets)
        {
            if (!Secrets.Contains(trimmed, StringComparer.Ordinal)) Secrets.Add(trimmed);
        }
    }

    public static string? Clean(string? value, int limit, bool keepLines = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = keepLines ? CleanBlock(value) : CleanInline(value);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= limit ? text : text[..Math.Max(0, limit - 1)].TrimEnd() + "…";
    }

    private static string CleanInline(string value)
    {
        var collapsed = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Apply(collapsed);
    }

    private static string CleanBlock(string value)
    {
        var lines = new List<string>();
        var blank = 0;
        foreach (var raw in value.Replace("\r\n", "\n").Split('\n'))
        {
            var line = Apply(raw.Trim());
            if (line.Length == 0)
            {
                blank++;
                if (blank <= 1 && lines.Count > 0) lines.Add("");
                continue;
            }
            blank = 0;
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static string Apply(string value)
    {
        lock (Secrets)
        {
            foreach (var secret in Secrets)
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        for (var i = 0; i < Patterns.Length; i++)
            value = Patterns[i].Replace(value, Replacements[i]);
        return value;
    }
}
