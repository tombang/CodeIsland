using System.Text.Json.Nodes;
using CodeIsland.Core;
using CodeIsland.Hooks;
using CodeIsland.Protocol;

namespace CodeIsland.Core.Tests;

public sealed class KiroHookTests
{
    [Theory]
    [InlineData("agentSpawn", AgentEventType.SessionStart)]
    [InlineData("userPromptSubmit", AgentEventType.Message)]
    [InlineData("preToolUse", AgentEventType.ToolStart)]
    [InlineData("postToolUse", AgentEventType.ToolEnd)]
    [InlineData("stop", AgentEventType.SessionEnd)]
    public void Normalize_KiroEvent_MapsToAgentEventType(string eventName, AgentEventType expectedType)
    {
        var agentEvent = RawAgentEventNormalizer.Normalize("""
            {"session_id":"kiro-session","cwd":"C:\\work","prompt":"look here","tool_name":"shell"}
            """, "kiro-cli", eventName);

        Assert.Equal(AgentKind.Kiro, agentEvent.Agent);
        Assert.Equal(expectedType, agentEvent.Type);
        Assert.Equal("kiro-session", agentEvent.SessionId);
    }

    [Fact]
    public void Install_KiroAgent_WritesFlatTimeoutEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeisland-kiro-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, ".kiro", "agents", "codeisland.json");
        var bridge = Path.Combine(root, "CodeIsland.Bridge.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(bridge, string.Empty);
            var tool = KnownTools.All.Single(value => value.Agent == AgentKind.Kiro);
            var installer = new NativeHookInstaller(new HookFileStore(Path.Combine(root, "backups")));
            installer.Install(config, tool, HookRegistration.Create(tool, bridge));
            installer.Install(config, tool, HookRegistration.Create(tool, bridge));

            var document = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            Assert.Equal("codeisland", document["name"]!.GetValue<string>());
            var entries = document["hooks"]!["agentSpawn"]!.AsArray();
            Assert.Single(entries);
            var entry = entries[0]!.AsObject();
            var command = entry["command"]!.GetValue<string>();
            Assert.Contains("--source kiro", command);
            Assert.Contains("--event agentSpawn", command);
            Assert.Equal("*", entry["matcher"]!.GetValue<string>());
            Assert.Equal(5000, entry["timeout_ms"]!.GetValue<int>());
            Assert.False(entry.ContainsKey("hooks"));
            Assert.False(entry.ContainsKey("timeout"));

            Assert.True(installer.Uninstall(config, tool, tool.HookMarker));
            document = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            Assert.Equal("codeisland", document["name"]!.GetValue<string>());
            Assert.False(document.ContainsKey("hooks"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
