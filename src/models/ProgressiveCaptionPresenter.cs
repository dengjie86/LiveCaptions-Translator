using System.Globalization;

namespace LiveCaptionsTranslator.models;

// Smooth the source text already returned by ASR, without changing translation input.
public sealed class ProgressiveCaptionPresenter
{
    private readonly PairedCaptionPresenter paired = new();
    private readonly ProgressiveCaptionText source = new();
    private CaptionPair? target;
    private CaptionPair? displayed;

    public long CurrentSequence => paired.CurrentSequence;
    public IReadOnlyList<CaptionPair> History => paired.History;
    public bool IsRevealing => !source.IsComplete;

    public CaptionPair? Advance(IReadOnlyList<CaptionFrame> frames, long nowMilliseconds)
    {
        // A ready translation still waits for its source to finish appearing. Only
        // then can the existing render acknowledgement start Chinese reading time.
        target = paired.Advance(frames, nowMilliseconds,
            allowAdvance: !IsRevealing && displayed is { IsPending: false }) ?? target;
        if (target == null) return null;

        source.SetTarget(target.Sequence, target.OriginalText, nowMilliseconds);
        string original = source.Advance(nowMilliseconds);
        var next = target with
        {
            OriginalText = original,
            TranslatedText = source.IsComplete ? target.TranslatedText : string.Empty,
            IsPending = !source.IsComplete || target.IsPending
        };
        if (next == displayed) return null;
        displayed = next;
        return next;
    }

    public void ConfirmDisplayed(long sequence, long nowMilliseconds)
    {
        if (displayed is { IsPending: false } && displayed.Sequence == sequence)
            paired.ConfirmDisplayed(sequence, nowMilliseconds);
    }

    public void Reset()
    {
        paired.Reset();
        source.Reset();
        target = null;
        displayed = null;
    }
}

public sealed class ProgressiveCaptionText
{
    private const double CharacterMilliseconds = 35;
    private const double MaximumRevealMilliseconds = 500;
    private long? sequence;
    private string target = string.Empty;
    private int[] boundaries = Array.Empty<int>();
    private int visibleCharacters;
    private double interval;
    private double nextCharacterAt;

    public string Text { get; private set; } = string.Empty;
    public bool IsComplete => visibleCharacters == boundaries.Length;

    public void SetTarget(long newSequence, string text, long nowMilliseconds)
    {
        if (sequence == newSequence && target == text) return;
        bool newSentence = sequence != newSequence;
        bool wasComplete = IsComplete;
        sequence = newSequence;
        target = text;
        boundaries = StringInfo.ParseCombiningCharacters(text);

        // Correct already visible characters in place. Never erase and replay the
        // entire prefix when ASR revises a word or shortens an interim result.
        visibleCharacters = newSentence ? 0 : Math.Min(visibleCharacters, boundaries.Length);
        if (visibleCharacters == 0 && boundaries.Length > 0) visibleCharacters = 1;
        UpdateText();
        if (IsComplete) return;

        interval = Math.Min(CharacterMilliseconds,
            MaximumRevealMilliseconds / (boundaries.Length - visibleCharacters));
        nextCharacterAt = newSentence || wasComplete
            ? nowMilliseconds + interval
            : Math.Min(nextCharacterAt, nowMilliseconds + interval);
    }

    public string Advance(long nowMilliseconds)
    {
        int previous = visibleCharacters;
        while (!IsComplete && nowMilliseconds >= nextCharacterAt)
        {
            visibleCharacters++;
            nextCharacterAt += interval;
        }
        if (previous != visibleCharacters) UpdateText();
        return Text;
    }

    private void UpdateText() => Text = IsComplete ? target : target[..boundaries[visibleCharacters]];

    public void Reset()
    {
        sequence = null;
        target = Text = string.Empty;
        boundaries = Array.Empty<int>();
        visibleCharacters = 0;
        interval = nextCharacterAt = 0;
    }
}
