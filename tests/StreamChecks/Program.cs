using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
string Chunk(string? content = null, string? reason = null, string? thinking = null) =>
    "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
        delta = new { content, reasoning_content = thinking }, finish_reason = reason } } }) + "\n\n";
async Task<bool> Fails(string input)
{
    try { await OpenAITranslationStream.ReadAsync(new StringReader(input), null); return false; }
    catch (IOException) { return true; }
}

var partials = new List<string>();
string result = await OpenAITranslationStream.ReadAsync(new StringReader(
    ": heartbeat\n\nevent: message\n" + Chunk(thinking: "internal reasoning") +
    Chunk("谢谢") + Chunk("你上舰") + Chunk("！", "stop") + "data: [DONE]\n\n"), partials.Add);
Check(result == "谢谢你上舰！" && partials.SequenceEqual(new[] { "谢谢", "谢谢你上舰", "谢谢你上舰！" }),
    "Only content grows in order; reasoning and heartbeat are ignored");
Check(await Fails(Chunk("未完成")), "EOF without a finish signal is an interrupted translation");
Check(await Fails(Chunk("未完成", "length") + "data: [DONE]\n\n"), "token truncation is not treated as success");
Check(await Fails(Chunk(reason: "content_filter")), "non-stop finish reasons remain failures");
Check(await Fails("data: {\"error\":{\"message\":\"test\"}}\n\n"), "server error events remain failures");
Check(await OpenAITranslationStream.ReadAsync(new StringReader(Chunk("完整", "stop")), null) == "完整",
    "normal stop supports providers closing without DONE");
Check(await OpenAITranslationStream.ReadAsync(new StringReader(Chunk("完整") + "data: [DONE]"), null) == "完整",
    "final event at EOF is consumed");
partials.Clear();
result = await OpenAITranslationStream.ReadAsync(new StringReader(
    Chunk("<thi") + Chunk("nk>hidden\nreasoning") + Chunk("</think>🔤 谢谢") +
    Chunk("舰长 🔤", "stop") + "data: [DONE]\n\n"), partials.Add);
Check(result == "谢谢舰长" && partials.SequenceEqual(new[] { "谢谢", "谢谢舰长" }),
    "split thinking tags and subtitle delimiters never appear on screen");
result = await OpenAITranslationStream.ReadAsync(new StringReader(
    "data: {\"choices\":\n" + "data: [{\"index\":0,\"delta\":{\"content\":\"多行\"},\"finish_reason\":\"stop\"}]}\n\n"), null);
Check(result == "多行", "SSE multi-line data fields assemble correctly");
using (var response = new HttpResponseMessage(HttpStatusCode.OK))
{
    response.Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(Chunk("中文😀", "stop"))));
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    result = await OpenAITranslationStream.ReadResponseAsync(response, null, default);
    Check(result == "中文😀", "fragmented UTF-8 bytes preserve Chinese and emoji");
}
using (var response = new HttpResponseMessage(HttpStatusCode.OK))
{
    response.Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"完整译文\"},\"finish_reason\":\"stop\"}]}");
    partials.Clear();
    result = await OpenAITranslationStream.ReadResponseAsync(response, partials.Add, default);
    Check(result == "完整译文" && partials.Single() == result, "JSON fallback works when server ignores stream=true");
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    bool canceled = false;
    try { await OpenAITranslationStream.ReadAsync(new StringReader(Chunk("不应显示", "stop")), _ => throw new Exception(), cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "cancellation prevents publishing late text");
}

// Hold the second packet until the first callback has actually happened.
var input = new GatedReader(new[] { Chunk("先到").TrimEnd(), "", Chunk("后到", "stop").TrimEnd(), "" });
partials.Clear();
var streamTask = OpenAITranslationStream.ReadAsync(input, text => { partials.Add(text); input.FirstPublished.TrySetResult(); });
await input.FirstPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
Check(!streamTask.IsCompleted && partials.Single() == "先到", "first Chinese is published before network response completes");
input.Release.TrySetResult();
Check(await streamTask == "先到后到", "later network data appends to the existing prefix");
var finishedReader = new GatedReader(new[] { Chunk("完成", "stop").TrimEnd(), "" });
Check(await OpenAITranslationStream.ReadAsync(finishedReader, null).WaitAsync(TimeSpan.FromSeconds(2)) == "完成",
    "normal finish does not wait for a stalled trailing DONE packet");

var queue = new TranslationTaskQueue();
queue.Update(new(1, "今日は", false));
Check(!queue.UpdatePartial(1, "今天"), "partial translations cannot attach to unfinalized recognition");
queue.Update(new(1, "今日は晴れです。", true));
queue.Update(new(2, "次の文です。", true));
Check(queue.UpdatePartial(1, "今天"), "finalized source accepts first partial translation");
Check(queue.Snapshot[0].TranslatedText == null, "partial content does not mark translation complete");
Check(!queue.UpdatePartial(1, "今日") && !queue.UpdatePartial(1, "今") && !queue.UpdatePartial(1, "今天"),
    "queue rejects replacement, shrink and duplicate partials");
var presenter = new ProgressiveCaptionPresenter();
presenter.Advance(queue.Snapshot, 0);
Check(presenter.Advance(queue.Snapshot, 500) is { IsPending: true, Sequence: 1, TranslatedText: "今天" },
    "streamed Chinese shows below its fully revealed source while still pending");
queue.Complete(2, "这是下一句。");
presenter.ConfirmDisplayed(1, 500);
Check(presenter.Advance(queue.Snapshot, 9000) == null && presenter.CurrentSequence == 1 && presenter.History.Count == 0,
    "slow streaming never advances to the next sentence or archives half a translation");
queue.UpdatePartial(1, "今天天气晴朗。");
Check(presenter.Advance(queue.Snapshot, 9100) is { Sequence: 1, IsPending: true, TranslatedText: "今天天气晴朗。" },
    "successive partials append in the same bilingual pair");
queue.Complete(1, "今天天气晴朗。");
Check(presenter.Advance(queue.Snapshot, 9200) is { Sequence: 1, IsPending: false },
    "completion is observable even when final text equals the last partial");
presenter.ConfirmDisplayed(1, 10000);
Check(presenter.Advance(queue.Snapshot, 11599) == null, "reading time begins after full translation render acknowledgement");
Check(presenter.Advance(queue.Snapshot, 11600) is { Sequence: 2 } && presenter.History.Single().TranslatedText == "今天天气晴朗。",
    "completed pair enters history only after the full reading interval");
Check(!queue.UpdatePartial(1, "今天天气晴朗。late"), "late packets cannot overwrite a completed result");
queue.Clear();
Check(!queue.UpdatePartial(2, "late"), "cleared sentence IDs reject late packets");
Check(RegexPatterns.NoticePrefixAndTranslation().Match("[100 ms] 第一行\n第二行").Groups[2].Value == "第一行\n第二行",
    "multi-line streamed translations display completely");
Console.WriteLine($"All {checks} streaming checks passed.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
}
sealed class GatedReader(string[] lines) : TextReader
{
    int position;
    public TaskCompletionSource FirstPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<string?> ReadLineAsync(CancellationToken token)
    {
        if (position == 2) await Release.Task.WaitAsync(token);
        return position < lines.Length ? lines[position++] : null;
    }
}
