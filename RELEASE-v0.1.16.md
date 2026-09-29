# 直播小帮手 v0.1.16 (MAUI + Blazor Hybrid 独立版)

本版本为核心稳定性与系统底层架构深度优化版本，彻底根除因音频会话终结器抛出未处理异常导致的程序致命硬崩溃（Fatal Process Terminating 0x80070490），并根治长时间直播运行后的非托管 COM 内存泄漏。

### 重点修复与改进

1. **彻底根除 GC 终结器致命硬崩溃事故 (Fatal Process Terminating 0x80070490 找不到元素)**：
   - **事故根因溯源**：深入排查两份用户上报的排障诊断日志，定位到陈旧第三方音频库 `CSCore 1.2.1.2` 的 `ComObject` 在 .NET 垃圾回收（GC）终结器线程执行析构清理时，向 Windows 系统调用 `IAudioSessionControl::UnregisterAudioSessionNotification` 返回 `0x80070490`（找不到元素）并向外抛出异常。
   - **进程级防护根除**：由于现代 .NET 运行时的保护机制，终结器线程（Finalizer Thread）逃逸的任何异常无法被常规代码 try-catch 拦截，直接触发进程级 Fast-Fail 强杀程序。本次更新彻底将 2017 年的旧库 `CSCore` 从工程依赖中剔除，完全消除了该不可捕获致命闪退风险与 `NU1701` 兼容告警。

2. **自研现代原生 Windows CoreAudio (WASAPI) 采样引擎 (`WasapiProcessMeter`)**：
   - **零事件注册**：彻底摒弃任何系统会话级变更监听注册（绝不调用易损坏的 `UnregisterAudioSessionNotification`），从源头拔除崩溃病因；
   - **零异常 [PreserveSig]**：底层 Win32 COM 接口方法全量标注 `[PreserveSig]`，底层状态完全以 HRESULT 整型返回，绝不向托管层抛出任何异常；
   - **零终结器干预**：无任何 C# 析构函数（终结器），GC Finalizer 线程永远不会被挂起或触发；
   - **PID 进程名智能缓存**：内置 5 秒 TTL 缓存层，显著降低每秒轮询查找音乐播放器进程名称时的系统 Win32 开销。

3. **根治长达数小时直播后的 700MB 非托管 COM 内存泄漏**：
   - 彻底修复系统媒体感知模块在每秒采样音频会话时未显式释放 COM RCW 指针导致的持续泄漏（排障日志显示运行 7.6 小时后内存从 260MB 泄漏至 712MB）；
   - 新引擎在遍历会话与峰值电平时采用严格的 `try...finally` 即用即销机制，显式调用 `Marshal.ReleaseComObject`，确保内存占用长期轻量且平稳。

4. **100% 独立运行库与隐私安全铁律保障**：
   - 严格遵循 `AGENTS.md` 铁律，纯独立运行库模式发布（包含完整 .NET 10 底层核心，文件数 >= 800，含 `coreclr.dll`）；
   - 发布前自动化脱敏扫描 0 敏感信息命中，确保用户纯净、安全、开箱即用。
