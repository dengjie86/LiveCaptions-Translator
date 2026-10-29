# LiveCaptions Translator · 双语字幕增强版

用于观看外语直播和视频的 Windows 桌面字幕工具：原文在上、译文在下，通过句子 ID 保持对应，并保留最近两组双语字幕帮助联系上下文。

基于 [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator) 二次开发。保留上游 Apache-2.0 许可证及作者署名，改动见 [CHANGELOG.md](CHANGELOG.md)。当前版本为 **1.8.0.0**。

[下载 Windows 版](https://github.com/dengjie86/LiveCaptions-Translator/releases/latest) · [反馈问题](https://github.com/dengjie86/LiveCaptions-Translator/issues)

## 当前功能

- **两种识别方式**：Windows 实时字幕，或本地 Qwen3-ASR 0.6B / 1.7B。Qwen 自动识别语音语言。
- **日中对齐，也支持其他语言**：原文逐步显示；中文流式追加；完整译文留出阅读时间后，再进入下一句。
- **滚动历史**：悬浮窗口保留最近两组完整原文与译文，避免翻译刚出现就被下一句挤掉。
- **跟随耳机切换**：Qwen 的 WASAPI 系统声音采集跟随 Windows 默认输出设备，重新连接音频时无需重载模型。
- **独立术语库**：支持原词／别名、指定译名、适用语境、目标语言、搜索和单独停用，内置 15 条直播用语示例。
- **流式翻译**：OpenAI 兼容服务（含 DeepSeek）逐段显示译文。断流时保留已收到的文字，并标明翻译未完成。
- **中文常用设置**：识别后端、翻译服务、字幕显示与术语管理使用中文说明。

语音识别、翻译和显示各自运行，通过句子 ID 配对。仅在原文定稿后发起翻译；流式输出缩短看到首段译文的等待时间，不代表语音尚未结束时就能得到完整译文。

## 下载和运行

已验证的环境为 **Windows 11 x64**。发布包包含 .NET 运行时，**不含 Qwen 模型、Python/GPU 环境、API key 或任何用户配置**。

1. 从 [Releases](https://github.com/dengjie86/LiveCaptions-Translator/releases/latest) 下载 Windows x64 ZIP，完整解压到可写目录，例如 `D:\Software\LiveCaptions-Translator`。
2. 双击 `Start-LiveCaptions.cmd`。启动器会固定工作目录，让配置和历史记录保存在解压目录中。
3. 在“设置”中选择识别方式和翻译服务。识别方式切换后需要重启程序。
4. 播放直播或视频，打开悬浮字幕窗口。

请保留 `qwen-asr` 文件夹与 EXE 的相对位置，单独复制 EXE 无法启动 Qwen 识别。

### 使用 Windows 实时字幕

先确保 Windows 实时字幕能正常显示原文，并安装需要的语音语言。在 Windows 实时字幕中选择源语言；在本程序中选择目标语言。使用这一后端时不需要另行安装 Qwen 模型或 Python。

### 使用本地 Qwen

当前识别脚本使用 **NVIDIA CUDA GPU**，尚未接入 CPU 或核显推理。先在独立 Conda 环境安装依赖；已有合适环境时可直接使用，不需要重复创建。

下面是已测试环境的安装示例，不会由程序自动执行：

```powershell
conda create -n qwen-asr python=3.11
conda activate qwen-asr
python -c "import sys; print(sys.executable)"
python -m pip install torch==2.11.0 torchvision==0.26.0 --index-url https://download.pytorch.org/whl/cu128
python -m pip install -r qwen-asr/requirements.txt
```

GPU 驱动需要兼容所选 PyTorch CUDA 运行时；项目不会自动修改驱动或 CUDA。

将以下模型仓库的完整文件下载到本地：

- [Qwen/Qwen3-ASR-0.6B](https://huggingface.co/Qwen/Qwen3-ASR-0.6B)
- [Qwen/Qwen3-ASR-1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)

默认目录结构：

```text
D:\Models\
  Qwen3-ASR-0.6B\config.json ...
  Qwen3-ASR-1.7B\config.json ...
```

可以只下载要使用的模型。程序每次只加载所选的一个模型，模型缺失时会显示错误，不会自动联网下载。

首次运行后，关闭程序，在本地 `setting.json` 中核对：

```json
{
  "QwenPythonPath": "D:\\Environment\\Miniforge3\\envs\\qwen-asr\\python.exe",
  "QwenModelsDirectory": "D:\\Models"
}
```

这只是两个字段的示意，**请修改现有字段，不要用这段内容覆盖整个配置文件**。随后在“设置 → 语音识别”中选择 Qwen 0.6B 或 1.7B，并重启程序。0.6B 是本项目日常低延迟测试使用的后端。

Qwen 采集的是 Windows 默认输出设备的系统声音，不是麦克风；观看直播时请将播放器输出到当前默认设备。

### 配置 DeepSeek

在“设置 → 翻译服务”选择 **OpenAI**，在 API 配置中填写：

| 字段 | 示例 |
| --- | --- |
| API URL | `https://api.deepseek.com/chat/completions` |
| 模型 | `deepseek-flash`（本版本实测使用） |
| API key | 自己申请的 key，仅填入本地配置 |
| 目标语言 | `zh-CN` |

模型名称及可用性以服务商账号为准。其他 OpenAI 兼容服务也可填写各自的地址、模型与 key。当前 OpenAI 兼容翻译自动使用流式接收；其他翻译服务保留原有响应方式。

### 使用术语库

入口：**设置 → 术语库 → 管理词条**。

- 多个写法用 `|` 分隔，例如 `bilibili | ビリビリ | B站`。
- 填写指定译名，例如“哔哩哔哩”，并用适用语境说明何时使用。
- 译文语言应与设置中的目标语言一致，例如 `zh-CN`。
- 点击“添加直播示例”可添加平台名、舰长／提督／总督、上舰、SC、联动和切片等示例。
- 保存后从下一次翻译请求起生效，无需重启；可以关闭整个术语库或单独停用词条。

术语库适用于 OpenAI 兼容服务、Ollama 和 OpenRouter。只发送本句匹配到的词条，语境由模型判断。它辅助翻译用词，不保证修正语音识别已经听错的内容。

## 数据保存与已知限制

- `setting.json` 保存设置和 API key，`glossary.json` 保存个人词条，`translation_history.db` 保存原文和译文历史。
- 本地 Qwen 识别不上传音频；使用云端翻译时，会发送本句文本、匹配术语及启用的上下文。
- 诊断日志只记录句子 ID、阶段、长度和时间，不记录字幕正文或凭据。
- Qwen 使用 Transformers 对增长中的语音片段做增量识别，尚未采用官方 vLLM 流式识别后端。
- 停顿断句、持续说话时的分段、网络耗时和阅读停留都会影响总体延迟。短语分段有可能切开语义；快速连续说话也可能造成显示积压。
- 中日混合语音、音乐、人声重叠和专有名词的识别、翻译仍可能出错。

## 从源码构建与检查

需要 Windows 和 .NET 8 SDK。在仓库根目录执行：

```powershell
dotnet run --project tests/AlignmentChecks/AlignmentChecks.csproj
dotnet run --project tests/StreamChecks/StreamChecks.csproj
dotnet run --project tests/GlossaryChecks/GlossaryChecks.csproj
dotnet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

术语库界面检查需要 Windows 桌面会话。音频采集测试需先进入项目对应的 Python 环境，但不会加载识别模型：

```powershell
python -c "import sys; print(sys.executable)"
python qwen-asr/test_capture.py
```

当前检查涵盖句子配对与阅读时间、流式协议与中断、术语匹配与保存、音频分段与设备切换。[docs/ci/build.yml](docs/ci/build.yml) 提供 GitHub Actions 模板；具备 workflow 写入权限后，可将其复制到 `.github/workflows/build.yml` 启用自动检查。当前发布已在本地完成上述验证。

## 致谢与许可证

原始应用由 [SakiRinn](https://github.com/SakiRinn) 与其他贡献者开发，本仓库是在其基础上维护的双语字幕增强版本，采用 [Apache-2.0](LICENSE)。详见 [NOTICE](NOTICE)。Qwen 模型与各翻译服务另行获取，遵循各自的许可证和服务条款。