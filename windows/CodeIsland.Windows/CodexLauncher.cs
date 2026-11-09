using System.Diagnostics;

namespace CodeIsland.Windows;

public sealed record CodexLaunch(string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Locates a real Codex executable. A npm <c>codex.cmd</c> shim cannot be the
/// process file name: stdin would not reliably reach Codex.
/// </summary>
public static class CodexLauncher
{
    public static CodexLaunch? Find()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CODEISLAND_CODEX");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim());
        candidates.AddRange(Where("codex"));
        candidates.AddRange(CommonPaths());
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var launch = Resolve(candidate);
            if (launch is not null) return launch;
        }
        return null;
    }

    public static CodexLaunch? Resolve(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) return null;
        var full = Path.GetFullPath(candidate);
        var extension = Path.GetExtension(full);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new CodexLaunch(full, []);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return null;

        var directory = Path.GetDirectoryName(full)!;
        var sibling = Path.Combine(directory, "codex.exe");
        if (File.Exists(sibling)) return new CodexLaunch(sibling, []);

        var package = Path.Combine(directory, "node_modules", "@openai", "codex");
        var nested = FindNestedExe(package);
        if (nested is not null) return new CodexLaunch(nested, []);

        var script = Path.Combine(package, "bin", "codex.js");
        if (!File.Exists(script)) return null;
        var node = Path.Combine(directory, "node.exe");
        if (!File.Exists(node))
            node = Where("node").FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? "";
        return File.Exists(node) ? new CodexLaunch(Path.GetFullPath(node), [script]) : null;
    }

    private static IEnumerable<string> CommonPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(local, "Programs", "codex", "codex.exe"),
            Path.Combine(local, "OpenAI", "Codex", "codex.exe"),
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(roaming, "npm", "codex.exe"),
            Path.Combine(roaming, "npm", "codex.cmd")
        ];
    }

    private static IEnumerable<string> Where(string name)
    {
        var results = new List<string>();
        try
        {
            var info = new ProcessStartInfo("where.exe", name)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(info);
            if (process is null) return results;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            foreach (var raw in text.Split('\n'))
            {
                var path = raw.Trim().TrimEnd('\r');
                if (path.Length > 3 && path[1] == ':' && File.Exists(path)) results.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }
        return results;
    }

    private static string? FindNestedExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                var files = Directory.GetFiles(dir, "codex.exe");
                if (files.Length > 0) return files[0];
                if (depth >= 4) continue;
                foreach (var sub in Directory.GetDirectories(dir)) queue.Enqueue((sub, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
