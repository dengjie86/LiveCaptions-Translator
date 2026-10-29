using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models;

public sealed record CaptionRequest(long Sequence, string OriginalText);
public sealed record CaptionFrame(long Sequence, string OriginalText, bool IsFinal,
    string? TranslatedText = null, string? PartialTranslation = null);
public sealed record CaptionPair(long Sequence, string OriginalText,
    string TranslatedText, bool IsPending);

// One visible sentence: live source -> final source + translation -> next sentence.
public sealed class PairedCaptionPresenter
{
    private static readonly Regex LatencyPrefix = new(@"^\[\s*\d+\s*ms\]\s*");
    private CaptionPair? current;
    private long holdUntil;
    private readonly List<CaptionPair> history = new();
    public long CurrentSequence => current?.Sequence ?? 0;
    public IReadOnlyList<CaptionPair> History => history.ToArray();

    public CaptionPair? Advance(IReadOnlyList<CaptionFrame> frames, long nowMilliseconds,
        bool allowAdvance = true)
    {
        if (frames.Count == 0) return null;
        int index = 0;
        while (index < frames.Count - 1 && frames[index].Sequence < CurrentSequence) index++;
        var frame = frames[index];
        if (allowAdvance && current is { IsPending: false } && frame.Sequence == current.Sequence &&
            nowMilliseconds >= holdUntil && index + 1 < frames.Count)
            frame = frames[index + 1];

        var next = new CaptionPair(frame.Sequence, frame.OriginalText,
            frame.TranslatedText ?? frame.PartialTranslation ?? string.Empty, frame.TranslatedText == null);
        if (next == current) return null;
        if (current is { IsPending: false } && next.Sequence > current.Sequence &&
            !string.IsNullOrWhiteSpace(current.TranslatedText))
        {
            history.Add(current with { TranslatedText = LatencyPrefix.Replace(current.TranslatedText, "") });
            if (history.Count > 2) history.RemoveAt(0);
        }
        if (!next.IsPending && (current == null || current.IsPending || current.Sequence != next.Sequence))
        {
            holdUntil = nowMilliseconds + ReadingMilliseconds(next.TranslatedText);
        }
        current = next;
        return next;
    }

    // A busy WPF dispatcher must not consume the reading time before rendering.
    public void ConfirmDisplayed(long sequence, long nowMilliseconds)
    {
        if (current is { IsPending: false } && current.Sequence == sequence)
            holdUntil = Math.Max(holdUntil, nowMilliseconds + ReadingMilliseconds(current.TranslatedText));
    }

    private static int ReadingMilliseconds(string translation)
    {
        var readableText = LatencyPrefix.Replace(translation, "");
        return Math.Clamp(readableText.Length * 80 + 400, 1600, 3500);
    }

    public void Reset()
    {
        current = null;
        holdUntil = 0;
        history.Clear();
    }
}
