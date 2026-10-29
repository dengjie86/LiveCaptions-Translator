using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models;

// Windows exposes a rolling text window, not an ASR isFinal event. Commit stable
// punctuation, or a longer text-idle interval; line wrapping is not punctuation.
public sealed class CaptionSentenceTracker
{
    public const int PunctuationStableMilliseconds = 350;
    public const int TextIdleMilliseconds = 1200;
    private const string Endings = ".?!。？！";
    private static readonly Regex LineWrap = new(@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}。、！？])\s*\r?\n\s*(?=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])");
    private static readonly Regex Whitespace = new(@"\s+");
    private string previous = string.Empty;
    private int consumed;
    private long nextSequence;
    private long activeSequence;
    private string activeText = string.Empty;
    private long lastChange;
    private string boundaryText = string.Empty;
    private long boundarySince;
    private long? emptySince;

    public static string Normalize(string text) => Whitespace.Replace(LineWrap.Replace(text, ""), " ").Trim();

    public IReadOnlyList<CaptionFrame> Observe(string text, long now)
    {
        var updates = new List<CaptionFrame>();
        text = Normalize(text);
        if (text.Length == 0)
        {
            emptySince ??= now;
            if (now - emptySince.Value < PunctuationStableMilliseconds)
                return updates;
            // UI Automation can briefly return an empty window during a repaint.
            // Only a sustained clear commits the remaining buffered sentences.
            while (consumed < previous.Length)
            {
                var tail = previous[consumed..];
                int end = FindSentenceEnd(tail);
                int length = end < 0 ? tail.Length : end;
                string source = tail[..length].Trim();
                if (source.Any(char.IsLetterOrDigit))
                {
                    if (activeSequence == 0) activeSequence = ++nextSequence;
                    activeText = source;
                    FinishActive(updates);
                }
                consumed += length;
            }
            previous = string.Empty;
            consumed = 0;
            return updates;
        }
        emptySince = null;

        if (previous.Length > 0 && text != previous)
        {
            int prefix = CommonPrefix(previous, text);
            if (prefix < consumed)
            {
                // Map a scrolled window while allowing its uncommitted end to change.
                int bestStart = -1, bestLength = prefix;
                for (int start = 1; start < previous.Length; start++)
                {
                    int length = CommonPrefix(previous.AsSpan(start), text.AsSpan());
                    bool endsSentence = length > 0 && Endings.Contains(text[length - 1]);
                    if ((length >= 4 || (length >= 2 && endsSentence)) &&
                        (length > bestLength || (length == bestLength && start + length == previous.Length)))
                    {
                        bestStart = start;
                        bestLength = length;
                    }
                }
                if (bestStart >= 0)
                    consumed = Math.Max(0, consumed - bestStart);
                else if (prefix >= 4)
                {
                    // Do not replay a late correction inside committed history.
                    int suffix = CommonSuffix(previous, text, prefix);
                    consumed = consumed < previous.Length && suffix >= previous.Length - consumed
                        ? Math.Clamp(consumed + text.Length - previous.Length, 0, text.Length)
                        : MapCommittedBoundary(previous, text, consumed);
                }
                else
                {
                    FinishActive(updates);
                    consumed = 0;
                }
            }
        }
        previous = text;
        consumed = Math.Min(consumed, text.Length);

        while (consumed < text.Length)
        {
            while (consumed < text.Length && (char.IsWhiteSpace(text[consumed]) || Endings.Contains(text[consumed])))
                consumed++;
            if (consumed == text.Length) break;

            var tail = text[consumed..];
            int end = FindSentenceEnd(tail);
            string source = (end < 0 ? tail : tail[..end]).Trim();
            if (activeSequence == 0) activeSequence = ++nextSequence;
            if (source != activeText)
            {
                activeText = source;
                lastChange = now;
                updates.Add(new(activeSequence, source, false));
            }
            if (end >= 0 && boundaryText != source)
            {
                boundaryText = source;
                boundarySince = now;
            }
            else if (end < 0) boundaryText = string.Empty;

            bool stableEnd = end >= 0 && now - boundarySince >= PunctuationStableMilliseconds;
            bool idleEnd = end < 0 && now - lastChange >= TextIdleMilliseconds;
            if (!stableEnd && !idleEnd) break;

            consumed += end < 0 ? tail.Length : end;
            FinishActive(updates);
        }
        return updates;
    }

    // Mode changes discard the existing window, without reusing sentence IDs.
    public void Reset(string text)
    {
        previous = Normalize(text);
        consumed = previous.Length;
        activeSequence = 0;
        activeText = boundaryText = string.Empty;
        emptySince = null;
    }

    private void FinishActive(List<CaptionFrame> updates)
    {
        if (activeSequence != 0 && activeText.Length > 0)
            updates.Add(new(activeSequence, activeText, true));
        activeSequence = 0;
        activeText = boundaryText = string.Empty;
    }

    private static int FindSentenceEnd(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!Endings.Contains(text[i])) continue;
            if (text[i] == '.' && i > 0 && i + 1 < text.Length &&
                char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
            int end = i + 1;
            while (end < text.Length && (Endings.Contains(text[end]) || "」』\"”’）)".Contains(text[end]))) end++;
            return end;
        }
        return -1;
    }

    private static int CommonPrefix(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        int length = 0;
        while (length < Math.Min(first.Length, second.Length) && first[length] == second[length]) length++;
        return length;
    }

    private static int MapCommittedBoundary(string oldText, string newText, int oldBoundary)
    {
        // When a final sentence is lengthened by a late ASR correction, do not
        // mistake its added characters for a new utterance. Match sentence ends.
        int oldOffset = 0, newOffset = 0;
        while (oldOffset < oldBoundary && newOffset < newText.Length)
        {
            int oldEnd = FindSentenceEnd(oldText[oldOffset..]);
            int newEnd = FindSentenceEnd(newText[newOffset..]);
            if (newEnd < 0) return newText.Length;
            newOffset += newEnd;
            if (oldEnd < 0 || oldOffset + oldEnd >= oldBoundary) return newOffset;
            oldOffset += oldEnd;
        }
        return newOffset;
    }

    private static int CommonSuffix(string first, string second, int prefix)
    {
        int length = 0;
        while (length < Math.Min(first.Length, second.Length) - prefix && first[^(length + 1)] == second[^(length + 1)]) length++;
        return length;
    }
}
