namespace CodeIsland.Windows;

public static class FeishuBridgeLocator
{
    public static string? Find()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CODEISLAND_FEISHU_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim());
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "feishu-bridge.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "helpers", "feishu-bridge.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
