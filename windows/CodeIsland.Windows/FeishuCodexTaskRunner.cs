using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeIsland.Windows;

public sealed record FeishuTaskCard(string Title, string Template, string Markdown);

/// <summary>
/// One Codex turn started from the bound Feishu chat. /cd remembers a directory,
/// /task starts work there, /stop cancels, and /status reports. Chat text never
/// becomes a shell command; Codex approvals still come back as permission cards.
/// </summary>
public sealed class FeishuCodexTaskRunner : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _remember;
    private readonly Action<FeishuTaskCard, string?, string> _onCard;
    private readonly Func<bool> _chinese;
    private readonly object _stderrLock = new();
    private Process? _process;
    private string? _cardId;
    private int _cardToken;
    private bool _createInFlight;
    private FeishuTaskCard? _latest;
    private string _lastActivity = "";
    private string _latestResult = "";
    private DateTime _lastActivitySent = DateTime.MinValue;
    private int _generation;
    private readonly StringBuilder _stderr = new();

    private readonly Action<Action> _invoke;

    public FeishuCodexTaskRunner(
        Func<AppSettings> settings,
        Action<AppSettings> remember,
        Action<FeishuTaskCard, string?, string> onCard,
        Func<bool> chinese,
        Action<Action> invoke)
    {
        _settings = settings;
        _remember = remember;
        _onCard = onCard;
        _chinese = chinese;
        _invoke = invoke;
    }

    public void Handle(string command, string text)
    {
        switch (command)
        {
            case "status": Flash(StatusCard()); break;
            case "cd": ChangeDirectory(text); break;
            case "stop": Stop(); break;
            case "task": Start(text); break;
            default:
                Flash(Card("CodeIsland", "grey", Zh ? "这条指令不会执行。" : "That command does not run anything."));
                break;
        }
    }

    public void NoteCard(string id, string token)
    {
        if (token != _cardToken.ToString() || _cardId is not null) return;
        _cardId = id;
        _createInFlight = false;
        if (_latest is not null) _onCard(_latest, id, token);
    }

    public void NoteSendFailed() => _createInFlight = false;

    private void Start(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length > 4000)
        {
            Flash(Card(Zh ? "任务太长" : "Task is too long", "red", Zh ? "请缩短到 4000 字以内。" : "Keep it under 4000 characters."));
            return;
        }
        if (prompt.Length == 0)
        {
            Flash(Card(Zh ? "用法" : "Usage", "blue", Zh ? "`/task 要做的事`\n或 `/task C:\\repo 要做的事`" : "`/task what to do`\nor `/task C:\\repo what to do`"));
            return;
        }
        if (_process is { HasExited: false })
        {
            Flash(Card(Zh ? "有任务在跑" : "A task is running", "orange", Zh ? "先发 `/stop`，或等这一轮结束。" : "Send `/stop`, or wait for this turn to finish."));
            return;
        }
        if (!_settings().FeishuTaskEnabled)
        {
            Flash(Card(Zh ? "任务未开启" : "Tasks are off", "orange", Zh ? "请在 Windows 的 CodeIsland 设置里打开「允许飞书发起 Codex 任务」。" : "Turn on Feishu Codex tasks in CodeIsland settings."));
            return;
        }
        var (overrideDirectory, taskPrompt) = FeishuTaskLines.Split(prompt, FeishuTaskLines.IsDirectory, FeishuTaskLines.Expand);
        if (overrideDirectory is not null) Remember(overrideDirectory);
        var directory = overrideDirectory ?? SavedDirectory();
        if (directory is null)
        {
            Flash(Card(Zh ? "还没有目录" : "No directory yet", "orange", Zh ? "先发送 `/cd C:\\repo`，或在设置里填写默认任务目录。" : "Send `/cd C:\\repo` first, or set a default task directory."));
            return;
        }
        var launch = CodexLauncher.Find();
        if (launch is null)
        {
            Flash(Card(Zh ? "找不到 Codex" : "Codex was not found", "red", Zh ? "找不到 codex.exe。如果只有 codex.cmd，请安装真正的 exe，或设置 CODEISLAND_CODEX。" : "codex.exe was not found. A codex.cmd shim cannot take the task on stdin."));
            return;
        }

        var generation = ++_generation;
        BeginCard();
        _lastActivity = "";
        _latestResult = "";
        lock (_stderrLock) _stderr.Clear();

        var info = new ProcessStartInfo(launch.FileName)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var prefix in launch.PrefixArguments) info.ArgumentList.Add(prefix);
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--color");
        info.ArgumentList.Add("never");
        info.ArgumentList.Add("--skip-git-repo-check");
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(directory);
        info.ArgumentList.Add("-");
        var path = info.Environment.TryGetValue("PATH", out var currentPath) ? currentPath : "";
        var launchDir = Path.GetDirectoryName(launch.FileName);
        if (!string.IsNullOrEmpty(launchDir)) info.Environment["PATH"] = launchDir + ";" + path;
        foreach (var name in ProviderEnvironment.Names())
        {
            if (info.Environment.TryGetValue(name, out var existing) && !string.IsNullOrWhiteSpace(existing))
            {
                FeishuRedactor.Remember(existing);
                continue;
            }
            var value = ProviderEnvironment.Lookup(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            info.Environment[name] = value;
            FeishuRedactor.Remember(value);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (generation != _generation || string.IsNullOrWhiteSpace(args.Data)) return;
            var line = args.Data;
            _invoke(() =>
            {
                if (generation == _generation) Consume(line);
            });
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            lock (_stderrLock)
            {
                _stderr.AppendLine(args.Data);
                if (_stderr.Length > 4000) _stderr.Remove(0, _stderr.Length - 4000);
            }
        };
        process.Exited += (_, _) =>
        {
            var status = -1;
            try { status = process.ExitCode; } catch (InvalidOperationException) { }
            _invoke(() =>
            {
                if (generation != _generation || !ReferenceEquals(_process, process)) return;
                _process = null;
                Finish(status);
            });
        };
        try
        {
            if (!process.Start()) throw new IOException("Codex did not start.");
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(taskPrompt);
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _generation++;
            _process = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx) when (killEx is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            Flash(Card(Zh ? "没有启动" : "Did not start", "red", Zh ? "Codex 没有启动。" : "Codex did not start."));
            return;
        }
        Show(Card(
            Zh ? "任务已开始" : "Task started",
            "blue",
            Body(Zh ? "已开始" : "Started", directory, taskPrompt, Zh ? "需要执行命令时仍会发审批卡片。发送 `/stop` 停止。" : "Command approvals still arrive as cards. Send `/stop` to cancel.")));
    }

    private void Stop()
    {
        if (_process is not { HasExited: false } process)
        {
            Flash(Card(Zh ? "没有任务" : "No task", "grey", Zh ? "现在没有正在跑的任务。" : "No task is running."));
            return;
        }
        _generation++;
        _process = null;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        Show(Card(Zh ? "已停止" : "Stopped", "grey", Zh ? "这一轮已停止。没点过的审批不会被自动允许。" : "This turn was stopped. An approval you did not tap is not allowed."));
    }

    private void Finish(int status)
    {
        string stderr;
        lock (_stderrLock) stderr = _stderr.ToString();
        var raw = string.IsNullOrWhiteSpace(_latestResult) ? stderr : _latestResult;
        var result = FeishuRedactor.Clean(raw, 1800, keepLines: true)
            ?? (Zh ? "任务结束，没有可显示的文字结果。" : "The task finished without a text result.");
        var directory = SavedDirectory() ?? "";
        if (status == 0)
            Show(Card(Zh ? "任务完成" : "Task finished", "green", Body(Zh ? "已完成" : "Finished", directory, result, null)));
        else
            Show(Card(Zh ? "任务失败" : "Task failed", "red", Body(Zh ? $"失败 · 退出码 {status}" : $"Failed · exit {status}", directory, result, null)));
        _lastActivity = "";
    }

    private void Consume(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var msg = root.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.Object ? message : default;
            var eventName = StringOf(root, "type") ?? (msg.ValueKind == JsonValueKind.Object ? StringOf(msg, "type") : null) ?? "";
            JsonElement item = default;
            var hasItem = root.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object
                || msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object;
            if (eventName is "agent_message" or "task_complete")
            {
                var text = StringOf(root, "message") ?? StringOf(root, "last_agent_message");
                if (msg.ValueKind == JsonValueKind.Object)
                    text ??= StringOf(msg, "message") ?? StringOf(msg, "last_agent_message");
                if (!string.IsNullOrWhiteSpace(text)) _latestResult = text;
            }
            if (eventName is "item.completed" or "item.started" or "item_completed" or "item_started")
            {
                if (!hasItem) return;
                var kind = StringOf(item, "type") ?? "";
                if (kind == "agent_message" && eventName.Contains("completed", StringComparison.Ordinal))
                {
                    var text = StringOf(item, "text") ?? StringOf(item, "message");
                    if (!string.IsNullOrWhiteSpace(text)) _latestResult = text;
                    return;
                }
                var activity = Activity(kind, item);
                if (activity is not null) NoteActivity(activity);
                return;
            }
            if (eventName is "error" or "stream_error" or "turn.failed")
            {
                var text = StringOf(root, "message");
                if (!string.IsNullOrWhiteSpace(text)) _latestResult = text;
            }
        }
        catch (JsonException)
        {
        }
    }

    private void NoteActivity(string activity)
    {
        if (activity == _lastActivity) return;
        _lastActivity = activity;
        var now = DateTime.UtcNow;
        if ((now - _lastActivitySent).TotalSeconds < 8) return;
        _lastActivitySent = now;
        var clean = FeishuRedactor.Clean(activity, 180) ?? activity;
        Show(Card(Zh ? "任务进行中" : "Task running", "wathet", Body(Zh ? "进行中" : "Running", SavedDirectory() ?? "", $"`{clean}`", null)));
    }

    private static string? Activity(string kind, JsonElement item)
    {
        switch (kind)
        {
            case "command_execution":
            case "local_shell_call":
                var command = StringOf(item, "command") ?? "";
                var first = command.Split('\n', 2)[0].Trim();
                return first.Length == 0 ? "command" : first;
            case "file_change":
            case "mcp_tool_call":
            case "web_search":
            case "web_search_call":
                return kind;
            default:
                return null;
        }
    }

    private void ChangeDirectory(string text)
    {
        var raw = text.Trim().Trim('"').Trim();
        if (raw.Length >= 2 && raw[0] == raw[^1] && raw[0] is '"' or '\'') raw = raw[1..^1];
        if (raw.Length == 0)
        {
            var current = SavedDirectory();
            if (current is null)
                Flash(Card(Zh ? "还没有目录" : "No directory yet", "orange", Zh ? "发送 `/cd C:\\repo`" : "Send `/cd C:\\repo`"));
            else
                Flash(Card(Zh ? "当前目录" : "Current directory", "blue", $"`{FeishuTaskLines.Display(current)}`\n\n" + (Zh ? "换目录：`/cd C:\\repo`" : "Change it with `/cd C:\\repo`")));
            return;
        }
        var expanded = FeishuTaskLines.Expand(raw);
        if (!FeishuTaskLines.IsDirectory(expanded))
        {
            Flash(Card(Zh ? "目录不存在" : "Directory not found", "red", $"`{FeishuTaskLines.Display(expanded)}`"));
            return;
        }
        Remember(expanded);
        Flash(Card(Zh ? "已切换目录" : "Directory changed", "turquoise", $"`{FeishuTaskLines.Display(expanded)}`\n\n" + (Zh ? "接下来的 `/task` 会在这里执行。" : "The next `/task` runs here.")));
    }

    private FeishuTaskCard StatusCard()
    {
        var directory = SavedDirectory();
        var shown = directory is null ? (Zh ? "未设置" : "not set") : $"`{FeishuTaskLines.Display(directory)}`";
        if (_process is { HasExited: false })
        {
            var activity = _lastActivity.Length == 0
                ? (Zh ? "还没有新的动作" : "no new action yet")
                : $"`{FeishuRedactor.Clean(_lastActivity, 180) ?? _lastActivity}`";
            return Card(Zh ? "任务进行中" : "Task running", "wathet", (Zh ? "**目录** " : "**Directory** ") + shown + "\n**" + (Zh ? "状态** " : "State** ") + activity);
        }
        return Card(Zh ? "空闲" : "Idle", "blue", (Zh ? "**目录** " : "**Directory** ") + shown + "\n\n" + (Zh ? "`/cd 目录` 切换，`/task 要做的事` 开始。" : "`/cd directory` switches, `/task what to do` starts."));
    }

    private void Remember(string directory)
    {
        var next = _settings() with { FeishuTaskDirectory = directory };
        _remember(next);
    }

    private string? SavedDirectory()
    {
        var raw = _settings().FeishuTaskDirectory?.Trim() ?? "";
        if (raw.Length == 0) return null;
        var expanded = FeishuTaskLines.Expand(raw);
        return FeishuTaskLines.IsDirectory(expanded) ? expanded : null;
    }

    private void BeginCard()
    {
        _cardToken++;
        _cardId = null;
        _createInFlight = false;
        _latest = null;
    }

    private void Show(FeishuTaskCard card)
    {
        _latest = card;
        var token = _cardToken.ToString();
        if (_cardId is not null) _onCard(card, _cardId, token);
        else if (!_createInFlight)
        {
            _createInFlight = true;
            _onCard(card, null, token);
        }
    }

    private void Flash(FeishuTaskCard card) => _onCard(card, null, "flash");

    private static FeishuTaskCard Card(string title, string template, string body) => new(title, template, body);

    private string Body(string state, string directory, string detail, string? footer)
    {
        var path = directory.Length == 0 ? (Zh ? "未设置" : "not set") : FeishuTaskLines.Display(directory);
        var clean = FeishuRedactor.Clean(detail, 2500, keepLines: true) ?? "";
        var lines = new List<string> { (Zh ? "**目录** `" : "**Directory** `") + path + "`", (Zh ? "**状态** " : "**State** ") + state, "", clean };
        if (!string.IsNullOrWhiteSpace(footer)) { lines.Add(""); lines.Add(footer); }
        return string.Join("\n", lines);
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private bool Zh => _chinese();

    public void Dispose()
    {
        _generation++;
        if (_process is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        _process = null;
    }
}
