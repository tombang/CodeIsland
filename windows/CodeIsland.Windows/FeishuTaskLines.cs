namespace CodeIsland.Windows;

public static class FeishuTaskLines
{
    public static (string? Directory, string Prompt) Split(string text, Func<string, bool> isDirectory, Func<string, string>? expand = null)
    {
        expand ??= static value => value;
        var trimmed = text.Trim();
        var (token, rest) = FirstToken(trimmed);
        var path = expand(StripQuotes(token));
        if (rest.Length > 0 && isDirectory(path)) return (path, rest);
        return (null, trimmed);
    }

    public static string Expand(string path)
    {
        var value = StripQuotes(path.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value == "~") value = home;
        else if (value.StartsWith("~/") || value.StartsWith("~\\"))
            value = Path.Combine(home, value[2..]);
        value = Environment.ExpandEnvironmentVariables(value);
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    public static bool IsDirectory(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public static string Display(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(path, home, StringComparison.OrdinalIgnoreCase)) return "~";
        var prefix = home.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "~" + Path.DirectorySeparatorChar + path[prefix.Length..];
        return path;
    }

    private static (string Token, string Remainder) FirstToken(string text)
    {
        if (text.Length == 0) return ("", "");
        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var end = text.IndexOf(quote, 1);
            if (end > 0)
            {
                var token = text[1..end];
                var rest = text[(end + 1)..].Trim();
                return (token, rest);
            }
        }
        var split = text.IndexOfAny([' ', '\t']);
        return split < 0 ? (text, "") : (text[..split], text[(split + 1)..].Trim());
    }

    private static string StripQuotes(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            return value[1..^1];
        return value;
    }
}
