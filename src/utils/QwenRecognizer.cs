using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils;

public sealed class QwenRecognizer
{
    private readonly object gate = new();
    private Process? process;

    public void Run(Setting settings, string backend, Action<CaptionFrame> onCaption,
        Action<string> onStatus, CancellationToken cancellation)
    {
        string modelName = backend == "Qwen1.7B" ? "Qwen3-ASR-1.7B" : "Qwen3-ASR-0.6B";
        // This app extracts its single-file bundle; external workers live beside the executable.
        string applicationDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string script = Path.Combine(applicationDirectory, "qwen-asr", "recognize.py");
        string model = Path.Combine(settings.QwenModelsDirectory, modelName);
        try
        {
            if (!File.Exists(settings.QwenPythonPath)) throw new FileNotFoundException("未找到 Qwen 专用 Python 环境");
            if (!File.Exists(script)) throw new FileNotFoundException("未找到 Qwen 识别脚本");
            if (!File.Exists(Path.Combine(model, "config.json"))) throw new FileNotFoundException("未找到本地模型文件");
            var start = new ProcessStartInfo(settings.QwenPythonPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
            // Replays short fixtures through the same recognizer/protocol for integration checks.
            string? fixture = Environment.GetEnvironmentVariable("LCT_QWEN_TEST_AUDIO");
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                start.ArgumentList.Add("--file");
                start.ArgumentList.Add(fixture);
            }
            onStatus($"{modelName}：正在加载…");
            using var child = new Process { StartInfo = start };
            lock (gate)
            {
                if (cancellation.IsCancellationRequested) return;
                child.Start();
                process = child;
            }
            string logDirectory = Path.Combine(Path.GetDirectoryName(script)!, "logs");
            Directory.CreateDirectory(logDirectory);
            var stderrTask = Task.Run(async () =>
            {
                using var log = new StreamWriter(Path.Combine(logDirectory, "recognizer.log"), false, Encoding.UTF8)
                    { AutoFlush = true };
                while (await child.StandardError.ReadLineAsync() is { } line)
                    await log.WriteLineAsync(line);
            });
            bool hadError = false;
            while (!cancellation.IsCancellationRequested && child.StandardOutput.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var message = document.RootElement;
                    var frame = QwenCaptionProtocol.ReadCaption(message);
                    if (frame != null) onCaption(frame);
                    else if (message.ValueKind == JsonValueKind.Object &&
                             message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                             type.GetString() is "status" or "error" &&
                             message.TryGetProperty("message", out var status) && status.ValueKind == JsonValueKind.String)
                    {
                        hadError |= type.GetString() == "error";
                        onStatus($"{modelName}：{status.GetString()}");
                    }
                }
                catch (JsonException) { /* Third-party import notices are not caption events. */ }
            }
            child.WaitForExit();
            stderrTask.GetAwaiter().GetResult();
            if (!cancellation.IsCancellationRequested && !hadError)
                onStatus(fixture != null && child.ExitCode == 0 ? $"{modelName}：测试音频完成" :
                    $"{modelName}：识别已停止，请重启程序（退出码 {child.ExitCode}）");
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            onStatus($"{modelName}：{exception.Message}");
        }
        finally
        {
            lock (gate) process = null;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (process == null) return;
            try
            {
                if (process.HasExited) return;
                process.StandardInput.WriteLine("stop");
                process.StandardInput.Flush();
                if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }
}
