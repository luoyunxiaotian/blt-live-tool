#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BiLi_live_Tool.Services.SystemMedia;

/// <summary>
/// 纯原生 Windows CoreAudio (WASAPI) 进程音频峰值与会话状态采样器。
/// 彻底替代陈旧存在终结器抛出异常崩溃漏洞的 CSCore 1.2.1.2。
/// 特点：
/// 1. 绝不注册任何 IAudioSessionEvents 通知（拔除 UnregisterAudioSessionNotification 0x80070490 崩溃病根）；
/// 2. 底层 COM 方法全量 [PreserveSig]，纯 HRESULT 整型返回，绝不向托管层抛出异常；
/// 3. 支持遍历系统所有活跃音频输出设备（eRender，活跃状态），全方位覆盖耳机、音箱、独立声卡及虚拟音频通道；
/// 4. 所有枚举和获取的 COM 接口通过 try...finally 显式调用 Marshal.ReleaseComObject 即用即销，彻底杜绝非托管内存泄漏；
/// 5. 无 C# 终结器（析构函数），GC Finalizer 线程永远不会介入，100% 免疫终结器线程 Fatal Crash。
/// </summary>
public static class WasapiProcessMeter
{
    private static readonly ConcurrentDictionary<uint, (string Name, long ExpireTick)> _procNameCache = new();
    private const int CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public readonly record struct ProcessAudioStatus(bool IsActive, float PeakVolume);

    public static float SamplePeakVolume(string[] processNames) => SampleProcessAudio(processNames).PeakVolume;

    /// <summary>枚举系统当前活跃音频渲染端点（用于诊断）。</summary>
    public static (int ActiveRenderDevices, string Summary) QueryAudioEndpoints()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devCol = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator == null) return (0, "无法初始化 MMDeviceEnumerator");

            int hr = enumerator.EnumAudioEndpoints(0, 1, out devCol);
            if (hr != 0 || devCol == null) return (0, $"获取音频端点集合失败 (hr=0x{hr:X8})");

            hr = devCol.GetCount(out int devCount);
            if (hr != 0) return (0, $"获取设备数失败 (hr=0x{hr:X8})");

            return (devCount, $"已枚举 {devCount} 个当前活跃音频渲染端点 (扬声器/耳机/虚拟通道)");
        }
        catch (Exception ex)
        {
            return (0, $"探测异常: {ex.Message}");
        }
        finally
        {
            if (devCol != null) Marshal.ReleaseComObject(devCol);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public static ProcessAudioStatus SampleProcessAudio(string[] processNames)
    {
        if (processNames == null || processNames.Length == 0) return new ProcessAudioStatus(false, 0f);

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devCol = null;

        try
        {
            // 1. 激活 MMDeviceEnumerator
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator == null) return new ProcessAudioStatus(false, 0f);

            // 2. 枚举所有当前处于活跃状态的音频渲染输出端点 (eRender = 0, DEVICE_STATE_ACTIVE = 1)
            int hr = enumerator.EnumAudioEndpoints(0, 1, out devCol);
            if (hr != 0 || devCol == null) return new ProcessAudioStatus(false, 0f);

            hr = devCol.GetCount(out int devCount);
            if (hr != 0 || devCount <= 0) return new ProcessAudioStatus(false, 0f);

            float maxPeak = 0f;
            bool isActive = false;
            var iid = IID_IAudioSessionManager2;

            // 3. 逐个设备遍历其音频会话
            for (int d = 0; d < devCount; d++)
            {
                IMMDevice? dev = null;
                IAudioSessionManager2? sessionManager = null;
                IAudioSessionEnumerator? sessionEnum = null;

                try
                {
                    if (devCol.Item(d, out dev) != 0 || dev == null) continue;

                    hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
                    if (hr != 0 || mgrObj == null) continue;
                    sessionManager = (IAudioSessionManager2)mgrObj;

                    hr = sessionManager.GetSessionEnumerator(out sessionEnum);
                    if (hr != 0 || sessionEnum == null) continue;

                    hr = sessionEnum.GetCount(out int sCount);
                    if (hr != 0 || sCount <= 0) continue;

                    for (int s = 0; s < sCount; s++)
                    {
                        IAudioSessionControl? session = null;
                        try
                        {
                            if (sessionEnum.GetSession(s, out session) != 0 || session == null) continue;

                            // 转换为 IAudioSessionControl2 获取 PID
                            if (session is IAudioSessionControl2 session2)
                            {
                                hr = session2.GetProcessId(out uint pid);
                                if (hr == 0 && pid > 0)
                                {
                                    string procName = ResolveProcessName(pid);
                                    if (!string.IsNullOrEmpty(procName) && processNames.Any(p => procName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        int stateHr = session2.GetState(out int state);
                                        if (stateHr == 0 && state == 1) // 1 = AudioSessionStateActive
                                        {
                                            isActive = true;
                                        }

                                        // 转换为 IAudioMeterInformation 获取音量峰值
                                        if (session is IAudioMeterInformation meter)
                                        {
                                            hr = meter.GetPeakValue(out float peak);
                                            if (hr == 0)
                                            {
                                                if (peak > 0.0001f) isActive = true;
                                                if (peak > maxPeak) maxPeak = peak;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 绝不向外扩散任何异常
                        }
                        finally
                        {
                            if (session != null)
                            {
                                try { Marshal.ReleaseComObject(session); } catch { }
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (sessionEnum != null) try { Marshal.ReleaseComObject(sessionEnum); } catch { }
                    if (sessionManager != null) try { Marshal.ReleaseComObject(sessionManager); } catch { }
                    if (dev != null) try { Marshal.ReleaseComObject(dev); } catch { }
                }
            }

            return new ProcessAudioStatus(isActive, maxPeak);
        }
        catch
        {
            return new ProcessAudioStatus(false, 0f);
        }
        finally
        {
            if (devCol != null) try { Marshal.ReleaseComObject(devCol); } catch { }
            if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
    }

    private static string ResolveProcessName(uint pid)
    {
        long now = Environment.TickCount64;
        if (_procNameCache.TryGetValue(pid, out var cached) && cached.ExpireTick > now)
        {
            return cached.Name;
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            _procNameCache[pid] = (name, now + 5000); // 5 秒 TTL 缓存
            return name;
        }
        catch
        {
            // 进程已退出或无权限访问
            return string.Empty;
        }
    }

    #region COM Interfaces

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterSessionNotification(IntPtr newNotifications);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid @override, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // Inherited from IAudioSessionControl (9 methods)
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid @override, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);

        // IAudioSessionControl2 specific methods
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out uint retVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out uint pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(uint u32ChannelCount, [In] IntPtr afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    }

    #endregion
}
#endif
