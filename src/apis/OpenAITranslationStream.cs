using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.apis;

/// <summary>Consumes content deltas only; partial text never implies a completed translation.</summary>
public static class OpenAITranslationStream
{
    public static async Task<string> ReadResponseAsync(HttpResponseMessage response,
        Action<string>? onPartial, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadAsync(reader, onPartial, token);
        }

        // Some OpenAI-compatible servers ignore stream=true and return normal JSON.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var choice = json.RootElement.GetProperty("choices")[0];
        CheckFinishReason(choice);
        string result = VisibleText(choice.GetProperty("message").GetProperty("content").GetString() ?? "");
        if (!string.IsNullOrWhiteSpace(result)) onPartial?.Invoke(result);
        return result;
    }

    public static async Task<string> ReadAsync(TextReader reader, Action<string>? onPartial,
        CancellationToken token = default)
    {
        var payload = new StringBuilder();
        var content = new StringBuilder();
        string published = "";
        bool stopped = false;
        bool done = false;

        void ConsumeEvent()
        {
            if (payload.Length == 0) return;
            string data = payload.ToString().Trim();
            payload.Clear();
            if (data == "[DONE]") { done = true; return; }
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new IOException("流式翻译返回错误。");
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    content.Append(text.GetString());
                    string visible = VisibleText(content.ToString());
                    if (visible != published)
                    {
                        if (!visible.StartsWith(published, StringComparison.Ordinal))
                            throw new IOException("流式译文发生不连续变化。");
                        token.ThrowIfCancellationRequested();
                        published = visible;
                        onPartial?.Invoke(visible);
                    }
                }
                stopped |= CheckFinishReason(choice);
            }
        }

        while (!done && !stopped)
        {
            token.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(token);
            if (line == null) { ConsumeEvent(); break; }
            if (line.Length == 0) ConsumeEvent();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (payload.Length > 0) payload.Append('\n');
                payload.Append(line.AsSpan(5).TrimStart());
            }
        }
        token.ThrowIfCancellationRequested();
        if (!done && !stopped) throw new IOException("翻译连接中断，译文未完成。");
        return published;
    }

    private static bool CheckFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind != JsonValueKind.String)
            return false;
        if (reason.GetString() != "stop") throw new IOException("译文未完整生成。");
        return true;
    }

    private static string VisibleText(string raw)
    {
        // Never reveal reasoning when compatible models put <think> blocks in content.
        string text = Regex.Replace(raw, "<think>.*?</think>", "", RegexOptions.Singleline);
        int thinking = text.IndexOf("<think>", StringComparison.Ordinal);
        if (thinking >= 0) text = text[..thinking];
        for (int length = 1; length < "<think>".Length; length++)
            if (text.EndsWith("<think>"[..length], StringComparison.Ordinal))
            {
                text = text[..^length];
                break;
            }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text.Replace("🔤", "").Trim();
    }
}
