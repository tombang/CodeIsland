namespace CodeIsland.Windows;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "zh-CN";
    public string DisplayMode { get; init; } = "primary";
    public bool LaunchAtLogin { get; init; }
    public bool SoundEnabled { get; init; } = true;
    public int MaxVisibleSessions { get; init; } = 5;
    public int EventHistoryLimit { get; init; } = 200;
    public int SessionCleanupMinutes { get; init; } = 30;
    public bool HideInFullscreen { get; init; } = true;
    public string ToggleShortcut { get; init; } = "Ctrl+Shift+I";
    public string ApproveShortcut { get; init; } = "Ctrl+Shift+A";
    public string DenyShortcut { get; init; } = "Ctrl+Shift+D";
    public bool FeishuEnabled { get; init; }
    public string FeishuAppId { get; init; } = "";
    public string FeishuOpenId { get; init; } = "";
    public bool FeishuTaskEnabled { get; init; }
    public string FeishuTaskDirectory { get; init; } = "";

    public AppSettings Validate() => this with
    {
        Language = Language is "zh-CN" or "en-US" or "system" ? Language : "system",
        DisplayMode = DisplayMode is "primary" or "cursor" ? DisplayMode : "primary",
        MaxVisibleSessions = Math.Clamp(MaxVisibleSessions, 1, 20),
        EventHistoryLimit = Math.Clamp(EventHistoryLimit, 20, 2000),
        SessionCleanupMinutes = Math.Clamp(SessionCleanupMinutes, 1, 1440),
        ToggleShortcut = NormalizeShortcut(ToggleShortcut, "Ctrl+Shift+I"),
        ApproveShortcut = NormalizeShortcut(ApproveShortcut, "Ctrl+Shift+A"),
        DenyShortcut = NormalizeShortcut(DenyShortcut, "Ctrl+Shift+D"),
        FeishuAppId = Bound(FeishuAppId, 200),
        FeishuOpenId = Bound(FeishuOpenId, 200),
        FeishuTaskDirectory = Bound(FeishuTaskDirectory, 500)
    };

    private static string Bound(string? value, int limit)
    {
        var text = (value ?? "").Trim();
        return text.Length <= limit ? text : text[..limit];
    }

    private static string NormalizeShortcut(string value, string fallback) =>
        HotKeyBinding.TryParse(value, out var binding) ? binding.ToString() : fallback;
}
