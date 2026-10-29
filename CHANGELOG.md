# 更新记录

## 1.8.0.0 — 2026-10-30

首次公开发布当前双语字幕增强版。基于上游 `SakiRinn/LiveCaptions-Translator` 的 `d6d8394`，保留原有 Apache-2.0 许可证与作者署名。

- 接入本地 Qwen3-ASR 0.6B / 1.7B，自动识别语言，保留 Windows 实时字幕识别入口。
- 将语音识别、翻译与显示分开处理，通过句子 ID 保持原文和译文对应。
- 原文逐字显示，翻译完成并留出阅读时间后切入下一句；上方保留最近两组完整双语字幕。
- 通过 WASAPI 跟随 Windows 默认声音输出，切换耳机时重新连接音频设备。
- 增加中文术语管理窗口，支持别名、指定译名、语境、目标语言、搜索及单独启停。
- OpenAI 兼容服务（含 DeepSeek）接入流式译文，逐步追加中文；中断结果标为未完成。
- 常用设置与字幕交互改为中文。当前项目、版本与更新链接指向本仓库。
- 提供句子对齐、流式协议、术语库与音频采集检查；发布包包含 .NET 运行时，不含模型、API key 或用户历史。

主要修改的上游文件：`Translator.cs`、`App.xaml.cs`、`TranslateAPI.cs`、`Caption.cs`、`Setting.cs`、`TranslationTaskQueue.cs`、`RegexPatterns.cs`、`UpdateUtil.cs`，以及设置、主窗口、悬浮窗口、关于页面和构建/版本配置。新增的识别、术语库、流式接收和显示状态类见 `src/` 与 `qwen-asr/`。
