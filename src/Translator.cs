// Modified for the dengjie86 bilingual subtitle edition; see CHANGELOG.md.
using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Threading;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly CancellationTokenSource recognitionCancellation = new();
        private static QwenRecognizer? qwenRecognizer;
        public static bool UsesQwen { get; }
        public static string ActiveRecognitionBackend { get; }

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;
        public static GlossaryStore Glossary { get; }

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
            Glossary = new GlossaryStore(Path.Combine(Directory.GetCurrentDirectory(), GlossaryStore.FileName));
            ActiveRecognitionBackend = setting.RecognitionBackend;
            UsesQwen = ActiveRecognitionBackend is "Qwen0.6B" or "Qwen1.7B";
            if (!UsesQwen)
            {
                window = LiveCaptionsHandler.LaunchLiveCaptions();
                LiveCaptionsHandler.FixLiveCaptions(Window);
                LiveCaptionsHandler.HideLiveCaptions(Window);
                caption.RecognitionStatus = "Windows 实时字幕";
            }
        }

        public static void SyncLoop()
        {
            if (UsesQwen)
            {
                qwenRecognizer = new QwenRecognizer();
                qwenRecognizer.Run(Setting!, ActiveRecognitionBackend, update =>
                {
                    Caption!.OriginalCaption = update.OriginalText;
                    translationTaskQueue.Update(update);
                    if (update.IsFinal)
                        CaptionDiagnostics.Write("recognized", update.Sequence, update.OriginalText.Length);
                }, status => App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    Caption!.RecognitionStatus = status)), recognitionCancellation.Token);
                return;
            }
            var tracker = new CaptionSentenceTracker();
            bool wasLogOnly = LogOnlyFlag;
            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(200);
                    continue;
                }
                string fullText;
                try
                {
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    tracker.Reset(string.Empty);
                    translationTaskQueue.Clear();
                    Thread.Sleep(100);
                    continue;
                }
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                if (wasLogOnly != LogOnlyFlag)
                {
                    wasLogOnly = LogOnlyFlag;
                    tracker.Reset(fullText);
                    translationTaskQueue.Clear();
                }
                foreach (var update in tracker.Observe(fullText, Environment.TickCount64))
                {
                    Caption.OriginalCaption = update.OriginalText;
                    translationTaskQueue.Update(update);
                }
                Thread.Sleep(25);
            }
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                if (!UsesQwen && Window == null)
                {
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.FixLiveCaptions(Window);
                    LiveCaptionsHandler.HideLiveCaptions(Window);
                }
                if (!translationTaskQueue.TryDequeue(out var request) || request == null)
                {
                    await Task.Delay(40);
                    continue;
                }
                if (LogOnlyFlag)
                {
                    if (translationTaskQueue.Complete(request.Sequence, string.Empty))
                        await LogOnly(request.OriginalText);
                    continue;
                }

                // Capture continues while final sentences are translated in FIFO order.
                CaptionDiagnostics.Write("translate-start", request.Sequence, request.OriginalText.Length);
                string translated;
                string partialTranslation = string.Empty;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                {
                    try
                    {
                        var result = await Translate(request.OriginalText, timeout.Token, partial =>
                        {
                            if (timeout.IsCancellationRequested || !translationTaskQueue.UpdatePartial(request.Sequence, partial))
                                return;
                            if (partialTranslation.Length == 0)
                                CaptionDiagnostics.Write("translate-first-chunk", request.Sequence,
                                    request.OriginalText.Length, partial.Length);
                            partialTranslation = partial;
                        }).WaitAsync(timeout.Token);
                        translated = result.Item1;
                        if (string.IsNullOrWhiteSpace(
                            RegexPatterns.NoticePrefixAndTranslation().Match(translated).Groups[2].Value))
                            translated = "[WARNING] 未返回译文";
                    }
                    catch (OperationCanceledException)
                    {
                        translated = "[WARNING] 翻译超时";
                    }
                    catch (Exception)
                    {
                        translated = "[ERROR] 翻译失败";
                    }
                }
                if (partialTranslation.Length > 0 &&
                    (translated.Contains("[WARNING]") || translated.Contains("[ERROR]")))
                    translated = partialTranslation + "\n[WARNING] 翻译未完成";
                bool accepted = translationTaskQueue.Complete(request.Sequence, translated);
                CaptionDiagnostics.Write(accepted ? "translate-complete" : "translate-discarded",
                    request.Sequence, request.OriginalText.Length, translated.Length,
                    accepted && !translated.Contains("[WARNING]") && !translated.Contains("[ERROR]"));
                if (accepted)
                {
                    await AddContexts();
                    await Log(request.OriginalText, translated);
                }
            }
        }

        public static void StopRecognition()
        {
            recognitionCancellation.Cancel();
            qwenRecognizer?.Stop();
        }

        public static async Task DisplayLoop()
        {
            var presenter = new ProgressiveCaptionPresenter();
            CaptionPair? lastDisplayed = null;
            bool wasLogOnly = LogOnlyFlag;
            while (true)
            {
                var frames = translationTaskQueue.Snapshot;
                if (wasLogOnly != LogOnlyFlag)
                {
                    wasLogOnly = LogOnlyFlag;
                    presenter.Reset();
                    lastDisplayed = null;
                }
                CaptionPair? pair;
                if (LogOnlyFlag)
                {
                    var latest = frames.LastOrDefault();
                    pair = new(latest?.Sequence ?? 0, latest?.OriginalText ?? Caption.OriginalCaption,
                        "[Paused]", false);
                    if (latest != null) translationTaskQueue.DiscardBefore(latest.Sequence);
                }
                else
                {
                    pair = presenter.Advance(frames, Environment.TickCount64);
                    translationTaskQueue.DiscardBefore(presenter.CurrentSequence);
                }
                if (pair != null && pair != lastDisplayed)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (lastDisplayed?.Sequence != pair.Sequence)
                            Caption.OverlayHistory = LogOnlyFlag ? Array.Empty<CaptionPair>() : presenter.History;
                        // Update a single source/translation pair atomically on the UI thread.
                        if (lastDisplayed?.OriginalText != pair.OriginalText)
                        {
                            Caption.OverlayOriginalCaption = pair.OriginalText;
                            Caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(
                                pair.OriginalText, TextUtil.VERYLONG_THRESHOLD);
                        }
                        if (lastDisplayed?.TranslatedText != pair.TranslatedText)
                        {
                            Caption.TranslatedCaption = pair.TranslatedText;
                            Caption.DisplayTranslatedCaption = TextUtil.ShortenDisplaySentence(
                                pair.TranslatedText, TextUtil.VERYLONG_THRESHOLD);
                            Caption.OverlayNoticePrefix = string.Empty;
                            if (pair.TranslatedText.Contains("[ERROR]") || pair.TranslatedText.Contains("[WARNING]") ||
                                pair.TranslatedText == "[Paused]")
                                Caption.OverlayCurrentTranslation = pair.TranslatedText;
                            else
                            {
                                var match = RegexPatterns.NoticePrefixAndTranslation().Match(pair.TranslatedText);
                                Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                                Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                            }
                        }
                    });
                    if (!pair.IsPending)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() =>
                            presenter.ConfirmDisplayed(pair.Sequence, Environment.TickCount64),
                            DispatcherPriority.Background);
                        CaptionDiagnostics.Write("shown", pair.Sequence, pair.OriginalText.Length,
                            pair.TranslatedText.Length);
                    }
                    else if (pair.Sequence != lastDisplayed?.Sequence)
                        CaptionDiagnostics.Write("source-shown", pair.Sequence, pair.OriginalText.Length);
                    if (pair.IsPending && pair.TranslatedText.Length > 0 &&
                        (lastDisplayed?.Sequence != pair.Sequence || lastDisplayed.TranslatedText.Length == 0))
                        CaptionDiagnostics.Write("translation-partial-shown", pair.Sequence,
                            pair.OriginalText.Length, pair.TranslatedText.Length);
                    lastDisplayed = pair;
                }
                await Task.Delay(presenter.IsRevealing ? 16 : 40);
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default,
            Action<string>? onPartial = null)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ApiName == "OpenAI" && onPartial != null)
                {
                    translatedText = await TranslateAPI.OpenAIStreaming(text, onPartial, token);
                }
                else if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error, 
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error, 
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption?.Contexts.Clear();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
            
            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);
            
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
