using System.IO;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils;

// Metadata only: no caption text, audio, prompts, credentials or API responses.
public static class CaptionDiagnostics
{
    private static readonly object gate = new();
    private static StreamWriter? writer;
    private static bool unavailable;
    private static readonly string directory = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "qwen-asr", "logs");

    public static void Write(string stage, long sequence, int sourceLength,
        int translationLength = 0, bool? success = null)
    {
        lock (gate)
        {
            if (unavailable) return;
            try
            {
                if (writer == null || writer.BaseStream.Length >= 2 * 1024 * 1024)
                {
                    writer?.Dispose();
                    writer = null;
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "caption-pipeline.jsonl");
                    if (File.Exists(path))
                        File.Move(path, Path.Combine(directory, "caption-pipeline.previous.jsonl"), true);
                    writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
                }
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, stage, sequence,
                    source_chars = sourceLength, translation_chars = translationLength, success
                }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never stop subtitles if the disk is unavailable.
                unavailable = true;
            }
        }
    }
}
