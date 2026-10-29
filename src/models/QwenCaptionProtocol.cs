using System.Text.Json;

namespace LiveCaptionsTranslator.models;

// The recognizer owns utterance IDs and finalization. A partial is never translated.
public static class QwenCaptionProtocol
{
    public static CaptionFrame? ReadCaption(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "caption" ||
            !message.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number ||
            !sequence.TryGetInt64(out long id) || id <= 0 ||
            !message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            !message.TryGetProperty("final", out var final) ||
            final.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        string source = text.GetString()!.Trim();
        return source.Length == 0 ? null : new CaptionFrame(id, source, final.GetBoolean());
    }
}
