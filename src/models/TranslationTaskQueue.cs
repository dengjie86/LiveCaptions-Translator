// Modified for the dengjie86 bilingual subtitle edition; see CHANGELOG.md.
namespace LiveCaptionsTranslator.models;

// Recognition, translation and display run independently, joined by sentence IDs.
public sealed class TranslationTaskQueue
{
    private readonly object gate = new();
    private readonly List<CaptionFrame> frames = new();
    private readonly Queue<CaptionRequest> requests = new();

    public IReadOnlyList<CaptionFrame> Snapshot
    {
        get { lock (gate) return frames.ToArray(); }
    }

    public void Update(CaptionFrame frame)
    {
        lock (gate)
        {
            int index = frames.FindIndex(item => item.Sequence == frame.Sequence);
            if (index >= 0)
            {
                if (frames[index].IsFinal) return;
                frames[index] = frame;
            }
            else frames.Add(frame);
            if (frame.IsFinal) requests.Enqueue(new(frame.Sequence, frame.OriginalText));
        }
    }

    public bool TryDequeue(out CaptionRequest? request)
    {
        lock (gate) return requests.TryDequeue(out request);
    }

    public bool UpdatePartial(long sequence, string translation)
    {
        lock (gate)
        {
            int index = frames.FindIndex(item => item.Sequence == sequence);
            if (index < 0 || !frames[index].IsFinal || frames[index].TranslatedText != null ||
                string.IsNullOrEmpty(translation)) return false;
            string previous = frames[index].PartialTranslation ?? string.Empty;
            if (translation.Length <= previous.Length || !translation.StartsWith(previous, StringComparison.Ordinal))
                return false;
            frames[index] = frames[index] with { PartialTranslation = translation };
            return true;
        }
    }

    public bool Complete(long sequence, string translation)
    {
        lock (gate)
        {
            int index = frames.FindIndex(item => item.Sequence == sequence);
            if (index < 0 || !frames[index].IsFinal || frames[index].TranslatedText != null) return false;
            frames[index] = frames[index] with { TranslatedText = translation, PartialTranslation = null };
            return true;
        }
    }

    public void DiscardBefore(long sequence)
    {
        lock (gate) frames.RemoveAll(item => item.Sequence < sequence);
    }

    public void Clear()
    {
        lock (gate)
        {
            frames.Clear();
            requests.Clear();
        }
    }
}
