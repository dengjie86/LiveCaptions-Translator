using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiveCaptionsTranslator.models;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace LiveCaptionsTranslator;

public partial class GlossaryWindow : FluentWindow
{
    private readonly GlossaryStore store;
    private readonly string targetLanguage;
    private GlossaryEntry? selected;
    private bool refreshing;
    private bool dirty;
    private bool ready;

    public GlossaryWindow(GlossaryStore store, string targetLanguage, bool supportsGlossary)
    {
        this.store = store;
        this.targetLanguage = targetLanguage;
        InitializeComponent();
        ready = true;
        ServiceHint.Text = supportsGlossary
            ? "适用于 DeepSeek / OpenAI / Ollama / OpenRouter。只提交本句匹配词条，由模型结合语境采用；不修改语音识别结果。"
            : "当前翻译服务不支持术语库；词条可正常管理，切换到 DeepSeek / OpenAI / Ollama / OpenRouter 后生效。";
        GlossaryEnabledSwitch.IsChecked = store.Snapshot().Enabled;
        GlossaryStatusText.Text = store.LoadError ?? "选择左侧词条编辑，或点击新增。术语保存在独立文件中。";
        GlossaryStatusText.ToolTip = store.FilePath;
        if (store.LoadError != null)
        {
            GlossaryEnabledSwitch.IsEnabled = false;
            EditorPanel.IsEnabled = false;
            AddExamplesButton.IsEnabled = false;
        }
        RefreshList();
        LoadEditor(null);
        Closing += OnClosing;
    }

    private void RefreshList()
    {
        if (!ready) return;
        refreshing = true;
        try
        {
            string query = GlossarySearchBox.Text.Trim();
            var entries = store.Snapshot().Entries;
            var visible = entries.Where(e => query.Length == 0 ||
                $"{e.Source} {e.Translation} {e.Context}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            GlossaryList.ItemsSource = visible;
            GlossaryList.SelectedItem = visible.FirstOrDefault(e => e.Id == selected?.Id);
            CountText.Text = $"显示 {visible.Length} / {entries.Length} 条";
        }
        finally { refreshing = false; }
    }

    private void LoadEditor(GlossaryEntry? entry)
    {
        refreshing = true;
        try
        {
            selected = entry;
            TermSourceBox.Text = entry?.Source ?? "";
            TermTranslationBox.Text = entry?.Translation ?? "";
            TermContextBox.Text = entry?.Context ?? "";
            TermLanguageBox.Text = entry?.TargetLanguage ?? targetLanguage;
            TermEnabledBox.IsChecked = entry?.Enabled ?? true;
            EditorTitle.Text = entry == null ? "新增词条" : "编辑词条";
            DeleteTermButton.IsEnabled = entry != null;
            dirty = false;
        }
        finally { refreshing = false; }
    }

    private void Draft_Changed(object sender, RoutedEventArgs e)
    {
        if (ready && !refreshing) dirty = true;
    }

    private bool TryChange(Action action, string message)
    {
        try
        {
            action();
            GlossaryStatusText.Text = message;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            GlossaryStatusText.Text = ex is ArgumentException ? ex.Message : "保存失败，请检查术语文件是否可写。原有词条未改动。";
            return false;
        }
    }

    private bool SaveDraft()
    {
        var entry = new GlossaryEntry
        {
            Id = selected?.Id ?? Guid.NewGuid(), Enabled = TermEnabledBox.IsChecked == true,
            Source = TermSourceBox.Text, Translation = TermTranslationBox.Text,
            Context = TermContextBox.Text, TargetLanguage = TermLanguageBox.Text
        };
        if (!TryChange(() => store.Upsert(entry), "词条已保存，从下一次翻译请求起生效。")) return false;
        LoadEditor(store.Snapshot().Entries.Single(e => e.Id == entry.Id));
        RefreshList();
        return true;
    }

    private bool CanLeaveEditor()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "当前词条有未保存的修改，是否保存？", "术语库",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveDraft();
    }

    private void Entry_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing) return;
        var next = GlossaryList.SelectedItem as GlossaryEntry;
        if (next?.Id == selected?.Id) return;
        if (!CanLeaveEditor()) { RefreshList(); return; }
        LoadEditor(next);
        RefreshList();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveEditor()) return;
        LoadEditor(null);
        RefreshList();
        TermSourceBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveDraft();
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshList();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!CanLeaveEditor()) e.Cancel = true; }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || MessageBox.Show(this, "删除当前词条？", "术语库", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (TryChange(() => store.Delete(selected.Id), "词条已删除。"))
        {
            LoadEditor(null);
            RefreshList();
        }
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        bool enabled = GlossaryEnabledSwitch.IsChecked == true;
        TryChange(() => store.SetEnabled(enabled), enabled ? "术语库已启用。" : "术语库已停用，词条仍保留。" );
        GlossaryEnabledSwitch.IsChecked = store.Snapshot().Enabled;
    }

    private void EntryEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox check || check.DataContext is not GlossaryEntry entry) return;
        bool enabled = check.IsChecked == true;
        if (TryChange(() => store.Upsert(entry with { Enabled = enabled }), enabled ? "词条已启用。" : "词条已停用。") && selected?.Id == entry.Id)
        {
            selected = selected with { Enabled = enabled };
            refreshing = true;
            TermEnabledBox.IsChecked = enabled;
            refreshing = false;
        }
        RefreshList();
    }

    private void Examples_Click(object sender, RoutedEventArgs e)
    {
        if (TryChange(store.AddStreamingExamples, "已添加缺少的直播示例，现有词条保持不变。")) RefreshList();
    }
}
