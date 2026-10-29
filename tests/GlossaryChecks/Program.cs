using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;

class Program
{
    static int checks;
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    [STAThread]
    static void Main()
    {
        string output = Path.GetFullPath("artifacts/glossary-checks");
        string fixture = Path.Combine(output, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var store = new GlossaryStore(Path.Combine(fixture, "glossary.json"));
        Check(store.LoadError == null && store.Snapshot().Entries.Length == 0, "missing file loads empty");
        store.AddStreamingExamples();
        Check(store.Snapshot().Entries.Length == 15, "streaming examples persisted");
        store.AddStreamingExamples();
        Check(store.Snapshot().Entries.Length == 15, "adding examples is idempotent");
        Check(store.Match("今日はビリビリで配信しています", "zh-CN").Count == 2, "Japanese aliases match source");
        Check(store.Match("ＢＩＬＩＢＩＬＩ", "ZH_cn").Count == 1, "Unicode width case and locale normalization");
        Check(store.Match("science description SCENE", "zh-CN").Count == 0, "SC never matches inside English words");
        Check(store.Match("SCありがとう", "zh-CN").Count == 1, "Latin acronym next to Japanese");
        Check(store.Match("ビリビリ", "en").Count == 0, "target language isolation");
        Check(store.Match("艦長になってくれてありがとう", "zh-CN")[0].Translation == "上舰", "specific phrase precedes shorter term");
        Check(store.BuildPromptSuffix("電気でビリビリする", "zh-CN").Contains("发麻"), "literal meaning context accompanies ambiguous alias");
        Check(store.BuildPromptSuffix("こんにちは", "zh-CN") == "", "no match adds no tokens");
        store.SetEnabled(false);
        Check(store.BuildPromptSuffix("ビリビリ", "zh-CN") == "", "global off disables prompt injection");
        store.SetEnabled(true);
        var platform = store.Snapshot().Entries[0];
        store.Upsert(platform with { Enabled = false });
        Check(store.Match("ビリビリ", "zh-CN").Count == 0, "per term off disables matching");
        store.Upsert(platform with { Translation = "B站" });
        store.AddStreamingExamples();
        Check(store.Match("ビリビリ", "zh-CN").Single().Translation == "B站", "examples preserve user edits");
        var custom = new GlossaryEntry { Source = " Foo｜Ｆoo\nBar ", Translation = "测试{0}", Context = "游戏角色" };
        store.Upsert(custom);
        Check(store.Match("foo", "zh-CN").Single().Translation == "测试{0}", "custom aliases match");
        Check(store.BuildPromptSuffix("Bar", "zh-CN").Contains("测试{0}"), "term braces remain literal");
        Check(new GlossaryStore(store.FilePath).Match("foo", "zh-CN").Count == 1, "reload preserves edits");
        bool invalid = false;
        try { store.Upsert(new GlossaryEntry { Source = " | ", Translation = "x" }); } catch (ArgumentException) { invalid = true; }
        Check(invalid && store.Snapshot().Entries.Length == 16, "invalid save retains previous entries");
        store.Delete(custom.Id);
        Check(store.Match("foo", "zh-CN").Count == 0, "delete removes matching");
        var copy = store.Snapshot(); copy.Entries[0] = custom;
        Check(store.Snapshot().Entries[0].Id == platform.Id, "snapshot array cannot mutate shared state");
        string badPath = Path.Combine(fixture, "malformed.json");
        File.WriteAllText(badPath, "{bad}");
        var bad = new GlossaryStore(badPath);
        bool blocked = false;
        try { bad.AddStreamingExamples(); } catch (InvalidOperationException) { blocked = true; }
        Check(bad.LoadError != null && blocked && File.ReadAllText(badPath) == "{bad}", "malformed data is protected from overwrites");
        File.WriteAllText(badPath, "{\"Version\":2,\"Entries\":[]}");
        Check(new GlossaryStore(badPath).LoadError != null, "unsupported file version cannot crash translator");
        File.WriteAllText(badPath, "{\"Entries\":null}");
        Check(new GlossaryStore(badPath).LoadError != null, "invalid schema cannot crash translator");
        using (var held = File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool denied = false;
            try { store.Delete(platform.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied = true; }
            Check(denied && store.Snapshot().Entries.Length == 15, "failed atomic save preserves in-memory entries");
        }
        Parallel.For(0, 20, i => { store.Match("ビリビリ", "zh-CN"); store.SetEnabled(i % 2 == 0); });
        store.SetEnabled(true);
        Check(new GlossaryStore(store.FilePath).LoadError == null, "concurrent reads and saves keep valid JSON");

        var exported = new GlossaryStore(Path.Combine(fixture, "defaults.json"));
        exported.AddStreamingExamples();
        File.Copy(exported.FilePath, Path.Combine(output, "streaming-glossary.json"), true);
        string[] cases = ["今日はビリビリで配信しています。", "ビリビリで艦長になってくれてありがとう！",
            "ビリビリの艦長、提督、総督の皆さん、ありがとうございます。", "明日はコラボ配信です。今日の切り抜きも見てね。",
            "電気に触れて、手がビリビリする。", "YouTubeのメンバーになってくれてありがとう。"];
        File.WriteAllText(Path.Combine(output, "prompt-suffixes.json"), JsonSerializer.Serialize(cases.Select(s => new
            { Source = s, Suffix = exported.BuildPromptSuffix(s, "zh-CN") })));

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        var window = new GlossaryWindow(store, "zh-CN", true);
        window.Show();
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var list = Control<ListBox>("GlossaryList");
        Check(list.Items.Count == 15, "Chinese management window loads entries");
        Control<System.Windows.Controls.TextBox>("GlossarySearchBox").Text = "哔哩";
        Check(list.Items.Count == 0, "search follows edited translation");
        Control<System.Windows.Controls.TextBox>("GlossarySearchBox").Text = "B站";
        Check(list.Items.Count > 0 && list.Items.Count < 15, "search includes source translation and context");
        Control<System.Windows.Controls.TextBox>("GlossarySearchBox").Text = "";
        Click("NewEntryButton");
        Control<System.Windows.Controls.TextBox>("TermSourceBox").Text = "テスト名前";
        Control<System.Windows.Controls.TextBox>("TermTranslationBox").Text = "测试名字";
        Click("SaveTermButton");
        Check(store.Match("テスト名前", "zh-CN").Single().Translation == "测试名字", "UI add and save reaches translator store");
        Control<System.Windows.Controls.TextBox>("TermTranslationBox").Text = "更新名字";
        Click("SaveTermButton");
        Check(new GlossaryStore(store.FilePath).Match("テスト名前", "zh-CN").Single().Translation == "更新名字", "UI edits persist to disk");
        Control<ToggleButton>("TermEnabledBox").IsChecked = false;
        Click("SaveTermButton");
        Check(store.Match("テスト名前", "zh-CN").Count == 0, "UI per-entry disable works");
        Control<ToggleButton>("GlossaryEnabledSwitch").IsChecked = false;
        Click("GlossaryEnabledSwitch");
        Check(store.Match("ビリビリ", "zh-CN").Count == 0, "UI global disable works");
        Control<ToggleButton>("GlossaryEnabledSwitch").IsChecked = true;
        Click("GlossaryEnabledSwitch");
        list.SelectedIndex = 0;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "glossary-window.png"))) encoder.Save(file);
        Check(Control<ButtonBase>("SaveTermButton").ActualWidth > 0, "editor layout renders");
        window.Close();
        Console.WriteLine($"{checks} checks passed");
    }
}
