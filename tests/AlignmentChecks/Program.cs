using LiveCaptionsTranslator.models;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
    checks++;
}

var tracker = new CaptionSentenceTracker();
var queue = new TranslationTaskQueue();
IReadOnlyList<CaptionFrame> Feed(string text, long time)
{
    var changes = tracker.Observe(text, time);
    foreach (var change in changes) queue.Update(change);
    return changes;
}
void Fresh()
{
    tracker = new();
    queue = new();
}
CaptionRequest Take()
{
    Check(queue.TryDequeue(out var request) && request != null, "A finalized request is available");
    return request!;
}

Feed("今", 0);
Check(queue.Snapshot.Single().OriginalText == "今" && !queue.TryDequeue(out _),
    "First Japanese character is available before translation");
Feed("今日は", 100);
Feed("今日は晴れ", 200);
Check(queue.Snapshot.Single().Sequence == 1 && !queue.TryDequeue(out _),
    "Interim revisions keep one ID and send no translation");
Feed("今日は晴れです。", 300);
Feed("今日は晴れです。", 649);
Check(!queue.TryDequeue(out _), "Punctuation is allowed a short correction interval");
Feed("今日は晴れです。", 650);
var first = Take();
Check(first.OriginalText == "今日は晴れです。", "Only the complete first sentence is translated");
Feed("今日は晴れです。", 3000);
Check(!queue.TryDequeue(out _), "An unchanged Windows caption is never translated twice");

var presenter = new PairedCaptionPresenter();
var pair = presenter.Advance(queue.Snapshot, 650);
Check(pair is { IsPending: true, OriginalText: "今日は晴れです。" },
    "Final Japanese remains visible while its translation is pending");
Feed("今日は晴れです。明日は雨です。", 3100);
Feed("今日は晴れです。明日は雨です。", 3450);
var second = Take();
queue.Complete(second.Sequence, "明天有雨。");
Check(presenter.Advance(queue.Snapshot, 3500) == null,
    "A later completed translation cannot overtake the first sentence");
queue.Complete(first.Sequence, "[100 ms] 今天天气晴朗。");
pair = presenter.Advance(queue.Snapshot, 3600);
Check(pair?.Sequence == first.Sequence && pair.TranslatedText.Contains("今天天气晴朗"),
    "Chinese fills the matching Japanese pair");
Check(presenter.Advance(queue.Snapshot, 5199) == null, "The first translation has a reading interval");
pair = presenter.Advance(queue.Snapshot, 5200);
Check(pair?.Sequence == second.Sequence && pair.TranslatedText == "明天有雨。",
    "The next complete pair appears in FIFO order");
Check(presenter.Advance(queue.Snapshot, 9000) == null, "The last pair stays visible without flicker");
queue.DiscardBefore(second.Sequence);
Check(queue.Snapshot.Count == 1, "Displayed history leaves the in-memory queue");
Check(!queue.Complete(first.Sequence, "late"), "Discarded results cannot return to the display");

Fresh();
Feed("今日は雨", 0);
Feed("今日は晴れ", 100);
Feed("今日は晴れです。", 300);
Feed("今日は晴れです。", 650);
Check(Take().OriginalText == "今日は晴れです。" && !queue.TryDequeue(out _),
    "Interim recognition corrections do not generate obsolete translations");

Fresh();
Feed("はい。はい。いいえ。", 0);
Feed("はい。はい。いいえ。", 350);
Feed("はい。はい。いいえ。", 700);
Feed("はい。はい。いいえ。", 1050);
var repeated = new List<CaptionRequest>();
while (queue.TryDequeue(out var item)) repeated.Add(item!);
Check(repeated.Select(r => r.OriginalText).SequenceEqual(new[] { "はい。", "はい。", "いいえ。" }) &&
    repeated.Select(r => r.Sequence).Distinct().Count() == 3,
    "Repeated short utterances and multi-sentence updates retain distinct IDs");

Fresh();
Feed("今日は晴れです。明日は雨です。次は", 0);
Feed("今日は晴れです。明日は雨です。次は", 350);
Feed("今日は晴れです。明日は雨です。次は", 700);
Feed("明日は雨です。次は雪", 800);
Feed("明日は雨です。次は雪です。", 900);
Feed("明日は雨です。次は雪です。", 1250);
var rolled = new List<string>();
while (queue.TryDequeue(out var item)) rolled.Add(item!.OriginalText);
Check(rolled.SequenceEqual(new[] { "今日は晴れです。", "明日は雨です。", "次は雪です。" }),
    "Scrolling the Windows caption window does not replay completed sentences");

Fresh();
Feed("これは句読点なし", 0);
Feed("これは句読点なし", 1199);
Check(!queue.TryDequeue(out _), "Unpunctuated source waits for text stability");
Feed("これは句読点なし", 1200);
Check(Take().OriginalText == "これは句読点なし", "Text-idle fallback finalizes unpunctuated Japanese");
Feed("これは句読点なし。続きです。", 1300);
Feed("これは句読点なし。続きです。", 1650);
Check(Take().OriginalText == "続きです。" && !queue.TryDequeue(out _),
    "Late punctuation does not replay a pause-finalized sentence");

Check(CaptionSentenceTracker.Normalize("これは途中で\n折り返された文章です") == "これは途中で折り返された文章です",
    "Japanese line wrapping never invents sentence punctuation");
Fresh();
Feed("価格は3.14ドルです。", 0);
Feed("価格は3.14ドルです。", 350);
Check(Take().OriginalText == "価格は3.14ドルです。", "A decimal point is not a sentence boundary");

Fresh();
Feed("未完了の文", 0);
Feed("", 100);
Check(!queue.TryDequeue(out _), "A transient empty UIA read does not finalize a sentence");
Feed("", 450);
Check(Take().OriginalText == "未完了の文", "Clearing the native window preserves its last utterance");
Feed("新しい文。", 500);
Feed("新しい文。", 850);
Check(Take().Sequence == 2, "Capture resumes with a new ID after the native window clears");

Fresh();
Feed("途中", 0);
presenter = new();
pair = presenter.Advance(queue.Snapshot, 0);
Feed("途中まで話します。", 100);
pair = presenter.Advance(queue.Snapshot, 100);
Check(pair is { OriginalText: "途中まで話します。", IsPending: true },
    "The visible Japanese grows live in its existing row");
Feed("途中まで話します。次です。", 450);
var errorFirst = Take();
queue.Complete(errorFirst.Sequence, "[WARNING] 翻译超时");
presenter.Advance(queue.Snapshot, 500);
Feed("途中まで話します。次です。", 800);
pair = presenter.Advance(queue.Snapshot, 2100);
Check(pair is { OriginalText: "次です。", IsPending: true, TranslatedText: "" },
    "A failed translation is shown once, then the next source starts with an empty Chinese row");
var active = queue.Snapshot.Last().Sequence;
queue.Clear();
tracker.Reset("途中まで話します。次です。");
presenter.Reset();
Check(!queue.Complete(active, "old result"), "A result from before pause/reset is ignored");
Feed("途中まで話します。次です。再開。", 2000);
Feed("途中まで話します。次です。再開。", 2350);
Check(Take().OriginalText == "再開。" && queue.Snapshot.Single().Sequence > active,
    "Resume does not replay the pre-pause caption window");

Fresh();
for (int i = 1; i <= 100; i++)
{
    queue.Update(new(i, $"sentence {i}", true));
    queue.Complete(i, $"translation {i}");
}
presenter = new();
var seen = new List<long>();
for (long time = 0; time <= 250000; time += 100)
{
    var change = presenter.Advance(queue.Snapshot, time);
    if (change != null) seen.Add(change.Sequence);
    queue.DiscardBefore(presenter.CurrentSequence);
}
Check(seen.SequenceEqual(Enumerable.Range(1, 100).Select(i => (long)i)),
    "A backlog of 100 completed sentences is displayed exactly once in order");
Fresh();
Feed("はい。はい。", 0);
Feed("はい。はい。", 350);
Feed("はい。はい。", 700);
while (queue.TryDequeue(out _)) { }
Feed("はい。いいえ。", 800);
Feed("はい。いいえ。", 1150);
Check(Take().OriginalText == "いいえ。" && !queue.TryDequeue(out _),
    "A scrolled window of repeated short sentences does not replay its overlapping prefix");

Fresh();
Feed("今日は", 0);
Feed("", 100);
Feed("今日は晴れ", 200);
Feed("今日は晴れです。", 300);
Feed("今日は晴れです。", 650);
Check(Take().OriginalText == "今日は晴れです。" && !queue.TryDequeue(out _),
    "Transient blank reads preserve one live sentence and one translation");

Fresh();
Feed("一つ目です。二つ目です。", 0);
Feed("", 100);
Feed("", 450);
Check(Take().OriginalText == "一つ目です。" && Take().OriginalText == "二つ目です。" && !queue.TryDequeue(out _),
    "A sustained clear flushes all sentences from a buffered multi-sentence window");

Fresh();
Feed("今日は晴れです。", 0);
Feed("今日は晴れです。", 350);
Take();
Feed("今日は晴れですね。明日は雨です。", 500);
Feed("今日は晴れですね。明日は雨です。", 850);
Check(Take().OriginalText == "明日は雨です。" && !queue.TryDequeue(out _),
    "A late correction lengthening a committed sentence does not create a translated fragment");

Fresh();
Feed("今日は晴れです", 0);
Feed("今日は晴れです", 1200);
Take();
Feed("今日は晴れそうですね。明日は", 1300);
Feed("今日は晴れそうですね。明日は雨です。", 1400);
Feed("今日は晴れそうですね。明日は雨です。", 1750);
Check(Take().OriginalText == "明日は雨です。" && !queue.TryDequeue(out _),
    "A corrected pause-finalized sentence is not replayed when final punctuation arrives");

CaptionFrame? QwenFrame(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return QwenCaptionProtocol.ReadCaption(document.RootElement);
}
Fresh();
var preview = QwenFrame("""{"type":"caption","sequence":1,"text":"今日は，大家好","final":false}""");
Check(preview is { IsFinal: false, OriginalText: "今日は，大家好" }, "Qwen preserves mixed-language live source text");
queue.Update(preview!);
Check(!queue.TryDequeue(out _), "Qwen partial results do not trigger translation");
var finalQwen = QwenFrame("""{"type":"caption","sequence":1,"text":"今日は，大家好。","final":true}""");
queue.Update(finalQwen!);
queue.Update(finalQwen!);
queue.Update(preview!);
Check(Take().OriginalText == "今日は，大家好。" && !queue.TryDequeue(out _),
    "A Qwen final result is translated once and cannot be overwritten by late previews");
Check(QwenFrame("""{"type":"status","message":"正在加载"}""") == null &&
      QwenFrame("""{"type":"caption","sequence":2,"text":" ","final":true}""") == null &&
      QwenFrame("""{"type":"caption","sequence":0,"text":"hello","final":true}""") == null,
    "Worker status, empty text and invalid IDs never become subtitle sentences");
queue.Update(new CaptionFrame(2, "次の文。", true));
queue.Complete(1, "今天，大家好。");
var qwenPresenter = new PairedCaptionPresenter();
Check(qwenPresenter.Advance(queue.Snapshot, 0) is { Sequence: 1, TranslatedText: "今天，大家好。" },
    "Qwen translation stays attached to its original sentence while the next waits");
Check(qwenPresenter.Advance(queue.Snapshot, 3000) is { Sequence: 2, TranslatedText: "" },
    "The next Qwen sentence does not inherit the previous Chinese translation");

Fresh();
queue.Update(new(1, "はい。", true));
queue.Update(new(2, "次です。", true));
queue.Complete(1, "[200 ms] 好。");
queue.Complete(2, "下一句。");
presenter = new();
presenter.Advance(queue.Snapshot, 0);
Check(presenter.Advance(queue.Snapshot, 1200) == null,
    "A short translation is not replaced after only one second");
presenter.ConfirmDisplayed(1, 5000);
Check(presenter.Advance(queue.Snapshot, 5040) == null && presenter.Advance(queue.Snapshot, 6599) == null,
    "A stalled UI still gives Chinese its full reading interval after it appears");
Check(presenter.Advance(queue.Snapshot, 6600) is { Sequence: 2, TranslatedText: "下一句。" },
    "The next translation appears once the actual display interval ends");
presenter.ConfirmDisplayed(1, 10000);
Check(presenter.Advance(queue.Snapshot, 10000) == null,
    "A stale display acknowledgement cannot bring back an earlier subtitle");

Fresh();
for (int i = 1; i <= 4; i++) queue.Update(new(i, $"日本語 {i}", true));
for (int i = 2; i <= 4; i++) queue.Complete(i, $"中文 {i}");
presenter = new();
Check(presenter.Advance(queue.Snapshot, 0) is { Sequence: 1, IsPending: true },
    "A delayed first translation keeps its Japanese active even when later sentences are ready");
Check(presenter.Advance(queue.Snapshot, 10000) == null && presenter.History.Count == 0,
    "Waiting Japanese is never scrolled away without a translation");
queue.Complete(1, "[9000 ms] 中文 1");
Check(presenter.Advance(queue.Snapshot, 10100) is { Sequence: 1, TranslatedText: "[9000 ms] 中文 1" },
    "A late translation fills the original Japanese pair");
Check(presenter.Advance(queue.Snapshot, 11000) == null,
    "A late translation receives reading time before scrolling");
presenter.Advance(queue.Snapshot, 12000);
Check(presenter.History.Single() is { Sequence: 1, OriginalText: "日本語 1", TranslatedText: "中文 1" },
    "The completed pair scrolls into history together, without latency metadata");
presenter.Advance(queue.Snapshot, 14000);
Check(presenter.History.Select(p => p.Sequence).SequenceEqual(new long[] { 1, 2 }),
    "Two previous pairs stay in chronological order above the current one");
presenter.Advance(queue.Snapshot, 16000);
Check(presenter.History.Select(p => p.Sequence).SequenceEqual(new long[] { 2, 3 }),
    "History stays bounded while preserving the two most recent complete pairs");
presenter.Advance(queue.Snapshot, 16040);
Check(presenter.History.Count == 2, "Unchanged frames do not duplicate history");
presenter.Reset();
Check(presenter.History.Count == 0, "Pause/reset clears the visible subtitle history");

var reveal = new ProgressiveCaptionText();
reveal.SetTarget(1, "今日は晴れです。", 0);
Check(reveal.Text == "今" && !reveal.IsComplete,
    "A recognition batch starts with its first character immediately");
Check(reveal.Advance(34) == "今" && reveal.Advance(35) == "今日" && reveal.Advance(70) == "今日は",
    "Available Japanese appears progressively at the normal character cadence");
reveal.SetTarget(1, "今日は晴れです。", 75);
Check(reveal.Advance(105) == "今日は晴",
    "Repeated snapshots do not restart the reveal clock or erase the prefix");
reveal.SetTarget(1, "今日は雨です。", 110);
Check(reveal.Text == "今日は雨" && reveal.Advance(140) == "今日は雨で",
    "An ASR correction replaces visible characters in place and continues revealing");
reveal.SetTarget(1, "今日", 145);
Check(reveal.Text == "今日" && reveal.IsComplete,
    "A shortened recognition result removes the stale suffix without replay");
reveal.SetTarget(1, "今日は晴れです。", 150);
Check(reveal.Text == "今日" && reveal.Advance(185) == "今日は",
    "New recognition extends an already displayed prefix without blanking it");
reveal.SetTarget(2, "明日は雨です。", 190);
Check(reveal.Text == "明" && reveal.Advance(225) == "明日",
    "A new sentence cancels the previous sentence's unfinished reveal");
reveal.Reset();
Check(reveal.Text == "" && reveal.IsComplete, "Reset clears all pending animation text");
reveal.SetTarget(3, "か\u3099😀👨‍👩‍👧‍👦好", 0);
Check(reveal.Text == "か\u3099" && reveal.Advance(35) == "か\u3099😀" &&
      reveal.Advance(70) == "か\u3099😀👨‍👩‍👧‍👦" && reveal.Advance(105).EndsWith("好"),
    "Combining kana, surrogate pairs and emoji sequences are never split");
reveal.SetTarget(4, new string('字', 101), 200);
Check(reveal.Advance(450).Length is > 1 and < 101 && reveal.Advance(701).Length == 101 && reveal.IsComplete,
    "Large recognition batches accelerate to finish within the bounded reveal time");
reveal.SetTarget(5, "途中から最後まで", 1000);
Check(reveal.Advance(10000) == "途中から最後まで" && reveal.IsComplete,
    "A UI stall catches up instead of creating a long character animation backlog");
reveal.SetTarget(6, "あいうえおかきくけこ", 0);
for (int time = 10; time <= 100; time += 10)
{
    reveal.SetTarget(6, "あいうえおかきくけこ" + new string('字', time / 10), time);
    reveal.Advance(time);
}
Check(reveal.Text.Length > 1, "Rapid preview extensions cannot postpone reveal indefinitely");

Fresh();
queue.Update(new(1, "今日は晴れです。", true));
queue.Update(new(2, "明日は雨です。", true));
queue.Complete(1, "[100 ms] 今天天气晴朗。");
queue.Complete(2, "明天有雨。");
var smooth = new ProgressiveCaptionPresenter();
Check(smooth.Advance(queue.Snapshot, 0) is { Sequence: 1, OriginalText: "今", TranslatedText: "", IsPending: true },
    "A pre-translated queued pair reveals Japanese before displaying its Chinese");
Check(queue.Snapshot[0].OriginalText == "今日は晴れです。",
    "Display smoothing never truncates recognition or translation input");
smooth.ConfirmDisplayed(1, 0);
Check(smooth.Advance(queue.Snapshot, 5000) is
        { Sequence: 1, OriginalText: "今日は晴れです。", TranslatedText: "[100 ms] 今天天气晴朗。", IsPending: false },
    "Even after a UI stall the first Chinese appears before the next sentence");
smooth.ConfirmDisplayed(1, 5000);
Check(smooth.Advance(queue.Snapshot, 6599) == null && smooth.History.Count == 0,
    "Chinese receives the full reading interval after reveal and actual rendering");
Check(smooth.Advance(queue.Snapshot, 6600) is { Sequence: 2, OriginalText: "明", TranslatedText: "" } &&
      smooth.History.Single() is { Sequence: 1, OriginalText: "今日は晴れです。", TranslatedText: "今天天气晴朗。" },
    "History archives the complete bilingual pair while the next source begins revealing");
Check(smooth.Advance(queue.Snapshot, 7100) is { Sequence: 2, OriginalText: "明日は雨です。", TranslatedText: "明天有雨。" },
    "Second Chinese fills only its own fully revealed source");
smooth.ConfirmDisplayed(2, 7100);
Check(smooth.Advance(queue.Snapshot, 7140) == null,
    "A stable completed pair causes no extra render or translation notifications");
smooth.Reset();
Check(smooth.History.Count == 0 && !smooth.IsRevealing && smooth.CurrentSequence == 0,
    "Pause/resume resets both the sentence presenter and reveal state");

Fresh();
queue.Update(new(1, "途中の日本語", false));
smooth = new();
smooth.Advance(queue.Snapshot, 0);
Check(smooth.Advance(queue.Snapshot, 500) is { OriginalText: "途中の日本語", IsPending: true },
    "Interim source can finish revealing while translation is still pending");
queue.Update(new(1, "途中の日本語です。", true));
queue.Complete(1, "这是正在说的日语。");
Check(smooth.Advance(queue.Snapshot, 1000) == null && smooth.IsRevealing,
    "A final extension continues from the visible prefix before its Chinese appears");
Check(smooth.Advance(queue.Snapshot, 1500) is { OriginalText: "途中の日本語です。", TranslatedText: "这是正在说的日语。" },
    "Final corrections and translation converge on the complete matching pair");

Console.WriteLine($"All {checks} checks passed.");
