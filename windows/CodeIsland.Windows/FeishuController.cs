using System.Text.Json;
using System.Windows.Threading;
using CodeIsland.Core;
using CodeIsland.Ipc;

namespace CodeIsland.Windows;

/// <summary>
/// Connects Windows permission requests and /task commands to the existing
/// feishu-bridge helper. A Feishu button can only allow once or deny. Chat
/// text, another account, and a missing request never become an allow.
/// </summary>
public sealed class FeishuController : IDisposable
{
    private readonly DesktopSessionStore _sessions;
    private readonly AppLogger _logger;
    private readonly Action<AppSettings> _persist;
    private readonly Dispatcher _dispatcher;
    private readonly FeishuBridgeClient _bridge = new();
    private readonly FeishuCodexTaskRunner _tasks;
    private readonly Dictionary<string, Notice> _notices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private AppSettings _settings;
    private int _generation;
    private int _restartAttempt;
    private int _secretGeneration;
    private int _startedSecretGeneration = -1;

    public FeishuController(
        DesktopSessionStore sessions,
        AppLogger logger,
        Action<AppSettings> persist,
        Dispatcher dispatcher,
        AppSettings settings)
    {
        _sessions = sessions;
        _logger = logger;
        _persist = persist;
        _dispatcher = dispatcher;
        _settings = settings;
        HasSecret = FeishuSecretStore.Load() is not null;
        RememberSecret(FeishuSecretStore.Load());
        _tasks = new FeishuCodexTaskRunner(() => _settings, RememberDirectory, SendTaskCard, () => Zh, action =>
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
        });
        _bridge.MessageReceived += OnBridgeMessage;
        _bridge.Exited += () => dispatcher.BeginInvoke(new Action(OnBridgeExited));
        sessions.PermissionSettled += OnPermissionSettled;
        StatusText = Zh
            ? "未连接。填写企业自建应用的 App ID 和 App Secret 后保存。"
            : "Not connected. Enter the self-built app's App ID and App Secret, then save.";
    }

    public string StatusText { get; private set; }
    public bool HasSecret { get; private set; }
    public bool IsPaired => !string.IsNullOrWhiteSpace(_settings.FeishuOpenId);
    public string? PairCandidateOpenId { get; private set; }
    public string AppId => _settings.FeishuAppId ?? "";
    public string OpenId => _settings.FeishuOpenId ?? "";
    public event Action? Changed;

    public void Apply(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => Apply(settings));
            return;
        }
        var previousId = _settings.FeishuAppId;
        var previousEnabled = _settings.FeishuEnabled;
        var previousOpen = _settings.FeishuOpenId;
        _settings = settings;
        HasSecret = FeishuSecretStore.Load() is not null;
        RememberSecret(FeishuSecretStore.Load());
        if (!settings.FeishuEnabled)
        {
            StopBridge();
            if (previousEnabled)
            {
                StatusText = Zh
                    ? "已关闭。待批的命令仍留在这台电脑上，不会自动允许。"
                    : "Turned off. Pending commands stay on this PC and are not allowed automatically.";
            }
            Changed?.Invoke();
            return;
        }
        var restart = !previousEnabled
            || !_bridge.IsRunning
            || !string.Equals(previousId, settings.FeishuAppId, StringComparison.Ordinal)
            || _startedSecretGeneration != _secretGeneration;
        if (restart) StartBridge();
        else if (!string.Equals(previousOpen, settings.FeishuOpenId, StringComparison.Ordinal))
            SendOpenId();
        Changed?.Invoke();
    }

    public bool TrySaveSecret(string secret)
    {
        if (!FeishuSecretStore.Save(secret)) return false;
        RememberSecret(secret);
        HasSecret = true;
        _secretGeneration++;
        return true;
    }

    public void ClearSecret()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(ClearSecret);
            return;
        }
        FeishuSecretStore.Delete();
        HasSecret = false;
        _secretGeneration++;
        StopBridge();
        StatusText = Zh ? "已删除本机保存的 App Secret。" : "Removed the saved App Secret.";
        Changed?.Invoke();
    }

    public void ArmBind()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(ArmBind);
            return;
        }
        if (!_bridge.IsRunning)
        {
            StatusText = Zh ? "先保存凭证并连上飞书，再开始绑定。" : "Save the credentials and connect before binding.";
            Changed?.Invoke();
            return;
        }
        _bridge.Send(new Dictionary<string, string> { ["op"] = "arm_bind" });
        StatusText = Zh
            ? "请用要绑定的飞书给机器人发一条私聊。文字不会批准命令。"
            : "Send the bot a private message from the Feishu account you want to bind. Text is never an approval.";
        Changed?.Invoke();
    }

    public void ConfirmBind()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(ConfirmBind);
            return;
        }
        var openId = PairCandidateOpenId?.Trim() ?? "";
        if (openId.Length == 0) return;
        _settings = _settings with { FeishuOpenId = openId };
        _persist(_settings);
        PairCandidateOpenId = null;
        _bridge.Send(new Dictionary<string, string> { ["op"] = "bind", ["open_id"] = openId });
        StatusText = Zh ? "已绑定。之后只有这个账号的按钮有效。" : "Bound. Only this account's buttons count.";
        Changed?.Invoke();
        Flush();
    }

    public void Unbind()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Unbind);
            return;
        }
        _settings = _settings with { FeishuOpenId = "" };
        _persist(_settings);
        PairCandidateOpenId = null;
        _bridge.Send(new Dictionary<string, string> { ["op"] = "unbind" });
        StatusText = Zh ? "已解除绑定。新的审批不会发到飞书。" : "Unbound. New approvals are not sent to Feishu.";
        Changed?.Invoke();
    }

    public void Offer(AgentEvent agentEvent)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => Offer(agentEvent));
            return;
        }
        if (!_settings.FeishuEnabled || agentEvent.Type != AgentEventType.PermissionRequest) return;
        if (string.IsNullOrWhiteSpace(agentEvent.EventId) || _settled.Contains(agentEvent.EventId)) return;
        _notices[agentEvent.EventId] = new Notice(
            FeishuRedactor.Clean(agentEvent.Agent.ToString(), 40) ?? "",
            FeishuRedactor.Clean(agentEvent.ToolName, 120) ?? "",
            FeishuRedactor.Clean(agentEvent.WorkingDirectory, 400) ?? "",
            FeishuPermissionText.Describe(agentEvent),
            FeishuRedactor.Clean(agentEvent.SessionId, 80) ?? "");
        Flush();
    }

    public void Dispose()
    {
        _sessions.PermissionSettled -= OnPermissionSettled;
        _lifetime.Cancel();
        _generation++;
        _tasks.Dispose();
        _bridge.Dispose();
        _lifetime.Dispose();
    }

    private void OnPermissionSettled(object? sender, DesktopSessionStore.SettledPermission settled)
    {
        if (_dispatcher.CheckAccess()) NoteSettled(settled.EventId, settled.Decision);
        else _dispatcher.BeginInvoke(() => NoteSettled(settled.EventId, settled.Decision));
    }

    private void NoteSettled(string eventId, string decision)
    {
        if (!_notices.ContainsKey(eventId)) return;
        if (!_settled.Add(eventId)) return;
        _notices.Remove(eventId);
        if (decision is not ("allow" or "deny")) decision = "gone";
        _bridge.Send(new Dictionary<string, string>
        {
            ["op"] = "resolved",
            ["request_id"] = eventId,
            ["decision"] = decision
        });
    }

    private void StartBridge()
    {
        _generation++;
        _startedSecretGeneration = _secretGeneration;
        Online = false;
        var executable = FeishuBridgeLocator.Find();
        if (executable is null)
        {
            _bridge.Stop();
            StatusText = Zh
                ? "找不到 feishu-bridge.exe。请安装带飞书 helper 的 Windows 包。"
                : "feishu-bridge.exe is missing. Install the Windows package that contains the Feishu helper.";
            Changed?.Invoke();
            return;
        }
        if (!_bridge.Start(executable))
        {
            StatusText = Zh ? "飞书 helper 没有启动。" : "The Feishu helper did not start.";
            Changed?.Invoke();
            return;
        }
        StatusText = Zh ? "正在连接飞书长连接。" : "Connecting to Feishu.";
        Changed?.Invoke();
        SendConfigure();
    }

    private void SendConfigure()
    {
        var secret = FeishuSecretStore.Load();
        var appId = _settings.FeishuAppId?.Trim() ?? "";
        if (appId.Length == 0 || string.IsNullOrEmpty(secret))
        {
            StatusText = Zh
                ? "已打开，但还没有可用的 App ID 和 App Secret。"
                : "Enabled, but there is no App ID and App Secret yet.";
            Changed?.Invoke();
            return;
        }
        RememberSecret(secret);
        _bridge.Send(new Dictionary<string, string>
        {
            ["op"] = "configure",
            ["app_id"] = appId,
            ["app_secret"] = secret,
            ["open_id"] = _settings.FeishuOpenId?.Trim() ?? ""
        });
    }

    private void SendOpenId()
    {
        var openId = _settings.FeishuOpenId?.Trim() ?? "";
        if (openId.Length == 0) _bridge.Send(new Dictionary<string, string> { ["op"] = "unbind" });
        else _bridge.Send(new Dictionary<string, string> { ["op"] = "bind", ["open_id"] = openId });
    }

    private void StopBridge()
    {
        _generation++;
        Online = false;
        _bridge.Stop();
    }

    private void OnBridgeExited()
    {
        Online = false;
        if (!_settings.FeishuEnabled) return;
        _restartAttempt++;
        var delay = Math.Min(30, Math.Max(2, _restartAttempt * 2));
        var token = _generation;
        StatusText = Zh
            ? $"飞书连接断了，{delay} 秒后重试。断线不会自动允许。"
            : $"Feishu disconnected. Retrying in {delay}s. A disconnect does not allow anything.";
        Changed?.Invoke();
        _ = RestartLater(delay, token);
    }

    private async Task RestartLater(int seconds, int token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _dispatcher.BeginInvoke(() =>
        {
            if (token != _generation || _bridge.IsRunning || !_settings.FeishuEnabled) return;
            StartBridge();
        });
    }

    private void OnBridgeMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("op", out var opValue)) return;
        var op = opValue.GetString() ?? "";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in message.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                fields[property.Name] = property.Value.GetString() ?? "";
        }
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => Handle(op, fields));
    }

    private void Handle(string op, IReadOnlyDictionary<string, string> fields)
    {
        switch (op)
        {
            case "status":
                var state = Field(fields, "state");
                var detail = Field(fields, "detail");
                Online = state == "online";
                if (Online) _restartAttempt = 0;
                StatusText = detail.Length == 0 ? state : detail;
                _logger.Info($"Feishu bridge state={state}.");
                if (Online) Flush();
                Changed?.Invoke();
                break;
            case "pair_candidate":
                var openId = Field(fields, "open_id");
                if (openId.Length == 0 || IsPaired) return;
                PairCandidateOpenId = openId;
                StatusText = Zh
                    ? "收到私聊。确认是你的账号后点「绑定这个账号」。"
                    : "A private message arrived. Confirm it is your account, then bind it.";
                Changed?.Invoke();
                break;
            case "card_action":
                ApplyCard(Field(fields, "request_id"), Field(fields, "decision"), Field(fields, "open_id"));
                break;
            case "task_command":
                var command = Field(fields, "command");
                if (command is not ("task" or "stop" or "status" or "cd")) return;
                if (!SameAccount(Field(fields, "open_id"))) return;
                _tasks.Handle(command, Field(fields, "text"));
                break;
            case "task_card_sent":
                var id = Field(fields, "message_id");
                if (id.Length > 0) _tasks.NoteCard(id, Field(fields, "token"));
                break;
            case "log":
                var message = Field(fields, "message");
                if (message.Contains("task card send failed", StringComparison.Ordinal)) _tasks.NoteSendFailed();
                if (message.Length > 0)
                {
                    StatusText = message;
                    Changed?.Invoke();
                }
                break;
        }
    }

    private void ApplyCard(string requestId, string decision, string openId)
    {
        if (decision is not ("allow" or "deny") || requestId.Length == 0) return;
        if (!SameAccount(openId) || _settled.Contains(requestId)) return;
        var action = decision == "allow" ? UserAction.Approve : UserAction.Deny;
        if (!_sessions.Resolve(requestId, action)) NoteSettled(requestId, "gone");
    }

    private bool SameAccount(string openId)
    {
        var paired = _settings.FeishuOpenId?.Trim() ?? "";
        return paired.Length > 0 && string.Equals(paired, openId.Trim(), StringComparison.Ordinal);
    }

    private void Flush()
    {
        if (!Online || !IsPaired || !_bridge.IsRunning) return;
        foreach (var pair in _notices.ToArray())
        {
            if (pair.Value.Sent) continue;
            _bridge.Send(new Dictionary<string, string>
            {
                ["op"] = "card",
                ["request_id"] = pair.Key,
                ["source"] = pair.Value.Source,
                ["tool"] = pair.Value.Tool,
                ["cwd"] = pair.Value.Cwd,
                ["command"] = pair.Value.Command,
                ["session"] = pair.Value.Session
            });
            pair.Value.Sent = true;
        }
    }

    private void SendTaskCard(FeishuTaskCard card, string? messageId, string token)
    {
        var markdown = FeishuRedactor.Clean(card.Markdown, 3500, keepLines: true) ?? card.Markdown;
        var fields = new Dictionary<string, string>
        {
            ["op"] = "task_card",
            ["title"] = card.Title,
            ["template"] = card.Template,
            ["markdown"] = markdown,
            ["token"] = token
        };
        if (!string.IsNullOrWhiteSpace(messageId)) fields["message_id"] = messageId;
        _bridge.Send(fields);
    }

    private void RememberDirectory(AppSettings settings)
    {
        _settings = settings;
        _persist(settings);
    }

    private static void RememberSecret(string? secret) => FeishuRedactor.Remember(secret);

    private bool Zh => L10n.ResolveLanguage(_settings.Language ?? "zh-CN") == "zh-CN";
    private bool Online { get; set; }

    private static string Field(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value.Trim() : "";

    private sealed class Notice
    {
        public Notice(string source, string tool, string cwd, string command, string session)
        {
            Source = source;
            Tool = tool;
            Cwd = cwd;
            Command = command;
            Session = session;
        }

        public string Source { get; }
        public string Tool { get; }
        public string Cwd { get; }
        public string Command { get; }
        public string Session { get; }
        public bool Sent { get; set; }
    }
}
