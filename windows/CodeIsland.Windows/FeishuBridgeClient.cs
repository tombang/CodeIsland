using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeIsland.Windows;

public sealed class FeishuBridgeClient : IDisposable
{
    private readonly object _writeLock = new();
    private Process? _process;
    private StreamWriter? _writer;
    private int _generation;

    public event Action<JsonElement>? MessageReceived;
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public bool Start(string executable)
    {
        Stop();
        var generation = ++_generation;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (generation != _generation || string.IsNullOrWhiteSpace(args.Data)) return;
            try
            {
                using var document = JsonDocument.Parse(args.Data);
                MessageReceived?.Invoke(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            if (generation == _generation) Exited?.Invoke();
        };
        try
        {
            if (!process.Start()) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return false;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        return true;
    }

    public void Send(IReadOnlyDictionary<string, string> fields)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(fields);
        }
        catch (NotSupportedException)
        {
            return;
        }
        lock (_writeLock)
        {
            var writer = _writer;
            if (writer is null || _process is not { HasExited: false }) return;
            try
            {
                writer.Write(json);
                writer.Write('\n');
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    public void Stop()
    {
        _generation++;
        var process = _process;
        _process = null;
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); } catch (IOException) { }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        process.Dispose();
    }

    public void Dispose() => Stop();
}
