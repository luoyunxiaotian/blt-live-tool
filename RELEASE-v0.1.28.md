# 直播小帮手 v0.1.28 (MAUI + Blazor Hybrid 独立版)

本版本重点解决用户在直播过程中遭遇的“语音念弹幕失效后降级到系统语音依然不念（静默哑巴）”连环故障，彻底修复底层 WinRT MediaPlayer 音量漏设与中文音色回退，增强 Edge-TTS 抖动重试韧性，上线控制台顶部降级状态警示与一键恢复按钮，并全面修复排障诊断日志中的格式化 Bug！

---

### 重点修复与改进

1. **彻底根治系统语音兜底静音缺陷（告别“降级后依然哑巴”）**：
   - **底层播放器音量补全**：修复 `NativeAudio.cs` 中 `SpeakSystemAsync` 只配置了 `SpeechSynthesizer.Options.AudioVolume`、遗漏了底层 WinRT `MediaPlayer` 音量设置的缺陷，显式设置 `_tts.Volume = Clamp01(volume)` 与语速，彻底根除 WinRT `MediaPlayer` 静音输出的致命死穴；
   - **系统普通话音色智能择优匹配**：重构 `PickSystemVoice` 匹配逻辑，当主播配置的是 Edge 在线音色（如 `zh-CN-XiaoyiNeural`）降级至系统语音时，自动智能优选普通话自然音色（`Huihui` / `Yaoyao` / `Xiaoxiao` / `Kangkang` 等），杜绝失配导致的静音；
   - **中文语音包缺失检测与告警**：当检测到主播操作系统（如部分精简版或非原生中文系统）未安装任何中文语音库时，在日志中输出清晰警告并引导主播在 Windows 设置中添加中文语音包。

2. **Edge-TTS 微服务与客户端调用双重韧性加固**：
   - **服务端网络抖动原地自动重试**：在 `edge_server.py` 的 `do_POST` 处理中增加对微软云端端点 WebSocket 连接超时与重置的短间隔原地重试（2 次尝试，间隔 300ms），规范化 stderr 详细异常输出；
   - **客户端智能重试与详尽错误保留**：在 `TtsSpeaker.cs` 中增加递增 500ms 重试延迟，并在请求失败时完整保留 HTTP 状态码与异常文本至 `LastEngineError`，方便追踪排查。

3. **控制台顶部与卡片降级感知与「一键恢复」按钮**：
   - **状态栏降级直观提示**：当首选引擎因连续网络失败触发临时降级时，在 `Tts.razor` 顶部控制条与左列语音引擎卡片中醒目呈现红色告警横幅（`★ 临时降级至 sys 语音兜底`）；
   - **一键重置恢复**：新增 `[立即恢复]` 按钮，主播点击即可立即抹除冷却计时并重试首选 Edge 引擎，无需重启客户端。

4. **排障诊断日志全面修复与多维增强**：
   - **格式化 Bug 彻底修复**：修复 `DiagnosticService.cs` 中 `DemotedAt` 打印字面值 `(触发于 HH:mm:ss)` 的类型 Bug，改用实际触发时间戳与格式化；修复 `EngineFails` 打印成字典类型名 `System.Collections.Generic.Dictionary...`，格式化为清晰的各引擎失败次数统计；
   - **排障维度强化**：新增输出 `最近引擎报错`（`LastEngineError`）、`最近合成结果`（`LastResult`）以及本机系统已安装语音清单与中文语音包自检。

5. **100% 独立运行库模式与脱敏安全保障**：
   - 严格遵循 `AGENTS.md` 全局铁律，100% Self-Contained 独立运行库模式编译发布（包含完整 .NET 10 底层核心，文件数达 1156 项，含 `coreclr.dll` 4.4MB、`clrjit.dll` 2.0MB）；
   - 配套生成 Inno Setup 安装包 `blt-live-tool-v0.1.28-maui-setup.exe` 与 NativeAOT 轻量引导器 `直播小帮手.exe`；
   - 发布前自动化脱敏扫描 2673 个文件 0 敏感信息命中（100% PASS），开箱即用，绝无运行库弹窗。
