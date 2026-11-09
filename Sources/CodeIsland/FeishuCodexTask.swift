import Foundation
import CodeIslandCore

/// One Codex task started from a bound Feishu chat.
///
/// `/cd` selects a directory, `/task` starts work there, `/stop` cancels, and
/// `/status` reports the current directory. Other text does not run anything.
/// Shell commands that Codex wants to run still come back through the normal
/// permission card.
struct FeishuTaskCard {
    var title: String
    var template: String
    var markdown: String
}

@MainActor
final class FeishuCodexTaskRunner {
    var onCard: ((FeishuTaskCard, String?, String) -> Void)?

    private var cardID: String?
    private var cardToken = 0
    private var createInFlight = false
    private var latestCard: FeishuTaskCard?

    private var process: Process?
    private var promptSummary = ""
    private var lastActivity = ""
    private var latestResult = ""
    private var startedAt: Date?
    private var lastActivitySent = Date.distantPast
    private var generation = 0
    private var splitter = TaskLineSplitter()
    private var stderrTail = TailBuffer()

    func handle(command: String, text: String) {
        switch command {
        case "status":
            flash(statusCard())
        case "cd":
            changeDirectory(text)
        case "stop":
            stop()
        case "task":
            start(text)
        default:
            flash(card(title: "CodeIsland", template: "grey", body: Self.zh ? "这条指令不会执行。" : "That command does not run anything."))
        }
    }

    func noteCardID(_ id: String, token: String) {
        guard token == String(cardToken), cardID == nil else { return }
        cardID = id
        createInFlight = false
        if let latestCard {
            onCard?(latestCard, id, token)
        }
    }

    private func start(_ prompt: String) {
        let prompt = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard prompt.count <= 4_000 else {
            flash(card(title: "任务太长", template: "red", body: Self.zh ? "请缩短到 4000 字以内。" : "Keep it under 4000 characters."))
            return
        }
        guard !prompt.isEmpty else {
            flash(card(title: "用法", template: "blue", body: Self.zh ? "`/task 要做的事`\n或 `/task ~/project/仓库 要做的事`" : "`/task what to do`"))
            return
        }
        if process?.isRunning == true {
            flash(card(title: "有任务在跑", template: "orange", body: Self.zh ? "先发 `/stop`，或等这一轮结束。" : "Send `/stop`, or wait for this turn to finish."))
            return
        }
        guard UserDefaults.standard.bool(forKey: SettingsKey.feishuTaskEnabled) else {
            flash(card(title: "任务未开启", template: "orange", body: Self.zh ? "请在 Mac 的 CodeIsland 设置里打开「允许飞书发起 Codex 任务」。" : "Turn on Feishu Codex tasks in CodeIsland settings."))
            return
        }
        let (override, taskPrompt) = splitTask(prompt)
        if let override {
            UserDefaults.standard.set(override, forKey: SettingsKey.feishuTaskCwd)
        }
        guard let expanded = override ?? savedDirectory() else {
            flash(card(title: "还没有目录", template: "orange", body: Self.zh ? "先发送 `/cd ~/project/某个仓库`。" : "Send `/cd ~/project/some-repo` first."))
            return
        }
        let taskText = taskPrompt
        guard let executable = Self.codexExecutable() else {
            flash(card(title: "找不到 Codex", template: "red", body: Self.zh ? "找不到 codex 命令。" : "The codex command was not found."))
            return
        }

        generation += 1
        let token = generation
        beginCard()
        promptSummary = String(taskText.prefix(80))
        lastActivity = ""
        latestResult = ""
        startedAt = Date()
        splitter = TaskLineSplitter()
        stderrTail = TailBuffer()

        let proc = Process()
        proc.executableURL = executable
        proc.currentDirectoryURL = URL(fileURLWithPath: expanded, isDirectory: true)
        proc.arguments = ["exec", "--json", "--color", "never", "--skip-git-repo-check", "-C", expanded, "-"]
        var env = ProcessInfo.processInfo.environment
        let prefix = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        env["PATH"] = prefix + ":" + (env["PATH"] ?? "")
        // A menu-bar app does not read ~/.zshrc. Codex's provider key is
        // exported there, so copy only the named variables into this process.
        for name in Self.providerEnvironmentNames() where (env[name] ?? "").isEmpty {
            if let value = Self.loginShellValue(name), !value.isEmpty {
                env[name] = value
            }
        }
        proc.environment = env

        let inPipe = Pipe()
        let outPipe = Pipe()
        let errPipe = Pipe()
        proc.standardInput = inPipe
        proc.standardOutput = outPipe
        proc.standardError = errPipe
        outPipe.fileHandleForReading.readabilityHandler = { [splitter] handle in
            let data = handle.availableData
            let lines = splitter.append(data)
            guard !lines.isEmpty else { return }
            Task { @MainActor in
                guard self.generation == token else { return }
                for line in lines { self.consume(line) }
            }
        }
        let stderrTail = self.stderrTail
        errPipe.fileHandleForReading.readabilityHandler = { handle in
            stderrTail.append(handle.availableData)
        }
        proc.terminationHandler = { finished in
            let status = finished.terminationStatus
            Task { @MainActor in
                guard self.generation == token else { return }
                self.process = nil
                self.finish(status: status)
            }
        }
        do {
            try proc.run()
        } catch {
            flash(card(title: "没有启动", template: "red", body: Self.zh ? "Codex 没有启动。" : "Codex did not start."))
            return
        }
        process = proc
        let payload = Data(taskText.utf8)
        try? inPipe.fileHandleForWriting.write(contentsOf: payload)
        try? inPipe.fileHandleForWriting.close()
        show(card(
            title: "任务已开始",
            template: "blue",
            body: taskBody(state: Self.zh ? "已开始" : "Started", directory: expanded, detail: taskText, footer: Self.zh ? "需要执行命令时仍会发审批卡片。发送 `/stop` 停止。" : "Command approvals still arrive as cards. Send `/stop` to cancel.")
        ))
    }

    private func stop() {
        guard let process, process.isRunning else {
            flash(card(title: "没有任务", template: "grey", body: Self.zh ? "现在没有正在跑的任务。" : "No task is running."))
            return
        }
        generation += 1
        process.terminate()
        self.process = nil
        show(card(title: "已停止", template: "grey", body: Self.zh ? "这一轮已停止。没点过的审批不会被自动允许。" : "This turn was stopped. An approval you did not tap is not allowed."))
    }

    private func finish(status: Int32) {
        let fallback = stderrTail.text().trimmingCharacters(in: .whitespacesAndNewlines)
        let raw = latestResult.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? fallback : latestResult
        let result = PushMessageFormatter.clean(raw, limit: 1800)
            ?? (Self.zh ? "任务结束，没有可显示的文字结果。" : "The task finished without a text result.")
        let directory = savedDirectory() ?? ""
        if status == 0 {
            show(card(title: "任务完成", template: "green", body: taskBody(state: Self.zh ? "已完成" : "Finished", directory: directory, detail: result, footer: nil)))
        } else {
            show(card(title: "任务失败", template: "red", body: taskBody(state: Self.zh ? "失败 · 退出码 \(status)" : "Failed · exit \(status)", directory: directory, detail: result, footer: nil)))
        }
        promptSummary = ""
        lastActivity = ""
        startedAt = nil
    }

    private func consume(_ line: String) {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        let msg = object["msg"] as? [String: Any]
        let event = (object["type"] as? String) ?? (msg?["type"] as? String) ?? ""
        let item = (object["item"] as? [String: Any]) ?? (msg?["item"] as? [String: Any])
        if event == "agent_message" || event == "task_complete" {
            let message = (object["message"] as? String) ?? (object["last_agent_message"] as? String) ?? (msg?["message"] as? String) ?? (msg?["last_agent_message"] as? String)
            if let message, !message.isEmpty { latestResult = message }
        }
        switch event {
        case "item.completed", "item.started", "item_completed", "item_started":
            guard let item else { return }
            let kind = (item["type"] as? String) ?? ""
            if kind == "agent_message", event.contains("completed") {
                if let text = item["text"] as? String ?? item["message"] as? String, !text.isEmpty {
                    latestResult = text
                }
                return
            }
            if let activity = Self.activity(kind: kind, item: item) {
                noteActivity(activity)
            }
        case "error", "stream_error", "turn.failed", "turn.aborted", "task_complete":
            if event == "task_complete" || event == "turn.completed" { return }
            if let message = object["message"] as? String, !message.isEmpty {
                latestResult = message
            }
        case "turn.completed":
            break
        default:
            break
        }
    }

    private func noteActivity(_ activity: String) {
        guard activity != lastActivity else { return }
        lastActivity = activity
        let now = Date()
        guard now.timeIntervalSince(lastActivitySent) >= 8 else { return }
        lastActivitySent = now
        let clean = PushMessageFormatter.clean(activity, limit: 180) ?? activity
        let directory = savedDirectory() ?? ""
        show(card(title: "任务进行中", template: "wathet", body: taskBody(state: Self.zh ? "进行中" : "Running", directory: directory, detail: "`\(clean)`", footer: nil)))
    }


    private func changeDirectory(_ text: String) {
        let raw = stripQuotes(text.trimmingCharacters(in: .whitespacesAndNewlines))
        if raw.isEmpty {
            if let current = savedDirectory() {
                flash(card(title: "当前目录", template: "blue", body: "`\(displayPath(current))`\n\n换目录：`/cd ~/project/仓库`"))
            } else {
                flash(card(title: "还没有目录", template: "orange", body: "发送 `/cd ~/project/仓库`"))
            }
            return
        }
        let expanded = expand(raw)
        guard isDirectory(expanded) else {
            flash(card(title: "目录不存在", template: "red", body: "`\(displayPath(expanded))`"))
            return
        }
        UserDefaults.standard.set(expanded, forKey: SettingsKey.feishuTaskCwd)
        flash(card(title: "已切换目录", template: "turquoise", body: "`\(displayPath(expanded))`\n\n接下来的 `/task` 会在这里执行。"))
    }

    private func statusCard() -> FeishuTaskCard {
        let directory = savedDirectory()
        let shown = directory.map(displayPath) ?? (Self.zh ? "未设置" : "not set")
        if process?.isRunning == true {
            let activity = lastActivity.isEmpty ? (Self.zh ? "还没有新的动作" : "no new action yet") : "`\(redact(lastActivity, limit: 180))`"
            return card(title: "任务进行中", template: "wathet", body: "**目录** `\(shown)`\n**状态** \(activity)")
        }
        return card(title: "空闲", template: "blue", body: "**目录** `\(shown)`\n\n`/cd 目录` 切换，`/task 要做的事` 开始。")
    }

    private func beginCard() {
        cardToken += 1
        cardID = nil
        createInFlight = false
        latestCard = nil
    }

    private func show(_ card: FeishuTaskCard) {
        latestCard = card
        let token = String(cardToken)
        if let cardID {
            onCard?(card, cardID, token)
        } else if !createInFlight {
            createInFlight = true
            onCard?(card, nil, token)
        }
    }

    private func flash(_ card: FeishuTaskCard) {
        onCard?(card, nil, "flash")
    }

    private func card(title: String, template: String, body: String) -> FeishuTaskCard {
        FeishuTaskCard(title: title, template: template, markdown: body)
    }

    private func taskBody(state: String, directory: String, detail: String, footer: String?) -> String {
        let path = directory.isEmpty ? (Self.zh ? "未设置" : "not set") : displayPath(directory)
        var lines = ["**目录** `\(path)`", "**状态** \(state)", "", redact(detail, limit: 2500)]
        if let footer, !footer.isEmpty { lines.append(""); lines.append(footer) }
        return lines.joined(separator: "\n")
    }

    private func savedDirectory() -> String? {
        let raw = (UserDefaults.standard.string(forKey: SettingsKey.feishuTaskCwd) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let expanded = expand(raw)
        return isDirectory(expanded) ? expanded : nil
    }

    private func splitTask(_ text: String) -> (String?, String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let (token, rest) = firstToken(trimmed)
        let path = expand(stripQuotes(token))
        if !rest.isEmpty, isDirectory(path) {
            return (path, rest)
        }
        return (nil, trimmed)
    }

    private func firstToken(_ text: String) -> (String, String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("\"" ) || trimmed.hasPrefix("'") {
            let quote = trimmed.first!
            let rest = trimmed.dropFirst()
            if let end = rest.firstIndex(of: quote) {
                let token = String(rest[..<end])
                let after = rest[rest.index(after: end)...].trimmingCharacters(in: .whitespaces)
                return (token, String(after))
            }
        }
        let parts = trimmed.split(maxSplits: 1, whereSeparator: \.isWhitespace)
        if parts.count == 2 { return (String(parts[0]), String(parts[1])) }
        return (trimmed, "")
    }

    private func stripQuotes(_ text: String) -> String {
        var value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.count >= 2, let first = value.first, let last = value.last, first == last, first == "\"" || first == "'" {
            value.removeFirst()
            value.removeLast()
        }
        return value
    }

    private func expand(_ path: String) -> String {
        (path as NSString).expandingTildeInPath
    }

    private func isDirectory(_ path: String) -> Bool {
        guard !path.isEmpty else { return false }
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDir) && isDir.boolValue
    }

    private func displayPath(_ path: String) -> String {
        let home = NSHomeDirectory()
        if path == home { return "~" }
        if path.hasPrefix(home + "/") { return "~" + String(path.dropFirst(home.count)) }
        return path
    }

    private func redact(_ text: String, limit: Int) -> String {
        PushMessageFormatter.clean(text, limit: limit) ?? text
    }

    private static func activity(kind: String, item: [String: Any]) -> String? {
        switch kind {
        case "command_execution", "local_shell_call":
            let command = (item["command"] as? String) ?? ""
            let first = command.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
            return first.isEmpty ? "command" : first
        case "file_change", "mcp_tool_call", "web_search", "web_search_call":
            return kind
        default:
            return nil
        }
    }

    /// Names from `env_key = "..."` in ~/.codex/config.toml, plus the company
    /// gateway variable this Mac already exports for terminal Codex.
    private static func providerEnvironmentNames() -> [String] {
        var names = ["API_GATEWAY_KEY"]
        let config = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".codex/config.toml")
        guard let text = try? String(contentsOf: config, encoding: .utf8) else { return names }
        for line in text.split(whereSeparator: \.isNewline) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("env_key") else { continue }
            let parts = trimmed.split(separator: "=", maxSplits: 1).map(String.init)
            guard parts.count == 2 else { continue }
            let name = parts[1].trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            guard name.range(of: "^[A-Za-z_][A-Za-z0-9_]*$", options: .regularExpression) != nil else { continue }
            if !names.contains(name) { names.append(name) }
        }
        return names
    }

    /// Read one environment variable from a login shell. The value is never logged.
    private static func loginShellValue(_ name: String) -> String? {
        guard name.range(of: "^[A-Za-z_][A-Za-z0-9_]*$", options: .regularExpression) != nil else { return nil }
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/zsh")
        // Non-interactive login shells do not read ~/.zshrc, which is where
        // the provider key is exported. Source that file explicitly.
        proc.arguments = ["-c", "source \"$HOME/.zshrc\" >/dev/null 2>&1; printf %s \"${\(name)}\""]
        let out = Pipe()
        proc.standardOutput = out
        proc.standardError = Pipe()
        do { try proc.run() } catch { return nil }
        proc.waitUntilExit()
        let value = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    private static func codexExecutable() -> URL? {
        let fm = FileManager.default
        let candidates = [
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex",
            NSHomeDirectory() + "/.local/bin/codex",
        ]
        return candidates.map { URL(fileURLWithPath: $0) }.first { fm.isExecutableFile(atPath: $0.path) }
    }

    private static var zh: Bool { L10n.shared.effectiveLanguage.hasPrefix("zh") }
}

private final class TaskLineSplitter: @unchecked Sendable {
    private var pending = Data()

    func append(_ data: Data) -> [String] {
        guard !data.isEmpty else { return [] }
        pending.append(data)
        var lines: [String] = []
        while let newline = pending.firstIndex(of: 0x0A) {
            let chunk = pending.subdata(in: pending.startIndex..<newline)
            let next = pending.index(after: newline)
            pending = next < pending.endIndex ? pending.subdata(in: next..<pending.endIndex) : Data()
            guard let text = String(data: chunk, encoding: .utf8) else { continue }
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { lines.append(trimmed) }
        }
        return lines
    }
}

private final class TailBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    private let limit = 4_000

    func append(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        lock.lock()
        data.append(chunk)
        if data.count > limit {
            data.removeFirst(data.count - limit)
        }
        lock.unlock()
    }

    func text() -> String {
        lock.lock()
        defer { lock.unlock() }
        return String(data: data, encoding: .utf8) ?? ""
    }
}
