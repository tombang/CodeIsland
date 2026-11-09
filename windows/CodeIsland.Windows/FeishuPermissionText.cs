using System.Text.Json;
using CodeIsland.Core;

namespace CodeIsland.Windows;

public static class FeishuPermissionText
{
    private static readonly string[] CommandKeys = ["command", "cmd", "CommandLine", "commandLine", "script"];
    private static readonly string[] PathKeys = ["file_path", "filePath", "path", "target_file", "TargetFile"];
    private static readonly string[] OtherKeys = ["url", "query", "pattern", "description", "prompt"];
    private static readonly string[] Containers = ["tool_input", "toolInput", "input", "parameters", "arguments", "tool_use"];

    public static string Describe(AgentEvent agentEvent)
    {
        string? detail = null;
        if (agentEvent.Payload is JsonElement payload && payload.ValueKind == JsonValueKind.Object)
            detail = FirstDetail(payload);
        detail ??= FirstNonEmpty(agentEvent.Text, agentEvent.Title);
        return FeishuRedactor.Clean(detail, 1500) ?? "";
    }

    private static string? FirstDetail(JsonElement payload)
    {
        var direct = FirstText(payload, CommandKeys) ?? FirstText(payload, PathKeys) ?? FirstText(payload, OtherKeys);
        if (direct is not null) return direct;
        foreach (var container in Containers)
        {
            if (!payload.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
            var nestedText = FirstText(nested, CommandKeys) ?? FirstText(nested, PathKeys) ?? FirstText(nested, OtherKeys);
            if (nestedText is not null) return nestedText;
        }
        return null;
    }

    private static string? FirstText(JsonElement element, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim())
                    .Where(item => !string.IsNullOrEmpty(item));
                var joined = string.Join(" ", parts!);
                if (!string.IsNullOrWhiteSpace(joined)) return joined;
            }
        }
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
