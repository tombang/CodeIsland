using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace CodeIsland.Windows;

/// <summary>
/// App Secret for the Feishu self-built app. It is a file in the user profile,
/// never a settings value and never the macOS keychain: an ad-hoc signature
/// change makes the keychain ask for a password the user cannot recover.
/// </summary>
public static class FeishuSecretStore
{
    private static string? _pathOverride;

    public static string FilePath => _pathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codeisland",
        "feishu-app-secret");

    public static void UsePathForTests(string? path) => _pathOverride = path;

    public static bool Save(string secret)
    {
        var value = secret.Trim();
        if (value.Length == 0) return false;
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, true);
            TryRestrict(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var secret = File.ReadAllText(FilePath).Trim();
            return secret.Length == 0 ? null : secret;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryRestrict(string path)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(sid)) return;
            var info = new ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(path);
            info.ArgumentList.Add("/grant:r");
            info.ArgumentList.Add($"*{sid}:(R,W)");
            using var process = Process.Start(info);
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
