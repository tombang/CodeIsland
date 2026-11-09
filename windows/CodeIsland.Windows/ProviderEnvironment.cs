using System.Text.RegularExpressions;

namespace CodeIsland.Windows;

/// <summary>
/// Finds provider environment names and values without printing them.
/// Profile files are parsed, not executed.
/// </summary>
public static class ProviderEnvironment
{
    private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> Names()
    {
        var names = new List<string> { "API_GATEWAY_KEY" };
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        if (!File.Exists(path)) return names;
        foreach (var name in NamesFromToml(ReadLimited(path)))
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        return names;
    }

    public static IEnumerable<string> NamesFromToml(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (!line.StartsWith("env_key", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var name = Unquote(line[(eq + 1)..]);
            if (NamePattern.IsMatch(name)) yield return name;
        }
    }

    public static string? Lookup(string name)
    {
        if (!NamePattern.IsMatch(name)) return null;
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(name, target)?.Trim();
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch (System.Security.SecurityException)
            {
            }
        }
        foreach (var path in ProfileFiles())
        {
            if (!File.Exists(path)) continue;
            var value = ReadAssignment(ReadLimited(path), name);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    public static string? ReadAssignment(string text, string name)
    {
        if (!NamePattern.IsMatch(name)) return null;
        string? found = null;
        var patterns = new[]
        {
            new Regex($@"^\s*\$env:{Regex.Escape(name)}\s*=\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex($@"^\s*(?:export\s+|set\s+)?{Regex.Escape(name)}\s*=\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            foreach (var pattern in patterns)
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                var value = Unquote(match.Groups[1].Value);
                if (value.Length > 0) found = value;
            }
        }
        return found;
    }

    private static IEnumerable<string> ProfileFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return
        [
            Path.Combine(home, ".codex", ".env"),
            Path.Combine(home, ".env"),
            Path.Combine(home, ".zshrc"),
            Path.Combine(home, ".bashrc"),
            Path.Combine(home, ".bash_profile"),
            Path.Combine(home, ".profile"),
            Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
        ];
    }

    private static string ReadLimited(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var budget = (int)Math.Min(stream.Length, 256 * 1024);
            var buffer = new byte[budget];
            var read = stream.Read(buffer, 0, budget);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Unquote(string raw)
    {
        var value = raw.Trim().TrimEnd(';').Trim();
        if (value.Length >= 2)
        {
            var quote = value[0];
            if ((quote == '"' || quote == '\'') && value[^1] == quote)
                value = value[1..^1];
        }
        return value.Trim();
    }
}
