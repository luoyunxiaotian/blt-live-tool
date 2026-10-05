#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BiLi_live_Tool.Services.SystemMedia;

/// <summary>
/// 纯原生 Windows CoreAudio (WASAPI) 进程音频峰值采样器。
/// 彻底替代陈旧存在终结器抛出异常崩溃漏洞的 CSCore 1.2.1.2。
/// 特点：
/// 1. 绝不注册任何 IAudioSessionEvents 通知（拔除 UnregisterAudioSessionNotification 0x80070490 崩溃病根）；
/// 2. 底层 COM 方法全量 [PreserveSig]，纯 HRESULT 整型返回，绝不向托管层抛出异常；
/// 3. 所有枚举和获取的 COM 接口通过 try...finally 显式调用 Marshal.ReleaseComObject 即用即销，彻底杜绝非托管内存泄漏；
/// 4. 无 C# 终结器（析构函数），GC Finalizer 线程永远不会介入，100% 免疫终结器线程 Fatal Crash。
/// </summary>
public static class WasapiProcessMeter
{
    private static readonly ConcurrentDictionary<uint, (string Name, long ExpireTick)> _procNameCache = new();
    private const int CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public readonly record struct ProcessAudioStatus(bool IsActive, float PeakVolume);

    public static float SamplePeakVolume(string[] processNames) => SampleProcessAudio(processNames).PeakVolume;

    public static ProcessAudioStatus SampleProcessAudio(string[] processNames)
    {
        if (processNames == null || processNames.Length == 0) return new ProcessAudioStatus(false, 0f);

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? defaultDevice = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            // 1. 激活 MMDeviceEnumerator
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator == null) return new ProcessAudioStatus(false, 0f);

            // 2. 获取默认音频渲染输出设备 (eRender = 0, eMultimedia = 1)
            int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice);
            if (hr != 0 || defaultDevice == null) return new ProcessAudioStatus(false, 0f);

            // 3. 激活 IAudioSessionManager2
            var iid = IID_IAudioSessionManager2;
            hr = defaultDevice.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
            if (hr != 0 || mgrObj == null) return new ProcessAudioStatus(false, 0f);
            sessionManager = (IAudioSessionManager2)mgrObj;

            // 4. 获取音频会话枚举器
            hr = sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return new ProcessAudioStatus(false, 0f);

            hr = sessionEnum.GetCount(out int count);
            if (hr != 0 || count <= 0) return new ProcessAudioStatus(false, 0f);

            float maxPeak = 0f;
            bool isActive = false;

            // 5. 遍历各个音频会话，按 ProcessId 匹配目标进程并读取状态与音量峰值
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? session = null;
                try
                {
                    hr = sessionEnum.GetSession(i, out session);
                    if (hr != 0 || session == null) continue;

                    // 转换为 IAudioSessionControl2 获取 PID
                    if (session is IAudioSessionControl2 session2)
                    {
                        hr = session2.GetProcessId(out uint pid);
                        if (hr == 0 && pid != 0)
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

            return new ProcessAudioStatus(isActive, maxPeak);
        }
        catch
        {
            return new ProcessAudioStatus(false, 0f);
        }
        finally
        {
            if (sessionEnum != null) try { Marshal.ReleaseComObject(sessionEnum); } catch { }
            if (sessionManager != null) try { Marshal.ReleaseComObject(sessionManager); } catch { }
            if (defaultDevice != null) try { Marshal.ReleaseComObject(defaultDevice); } catch { }
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
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr endpoints);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
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
    [Guid("E2F5EE11-2070-4DC5-8663-C614229249E8")]
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
    [Guid("bfb7ff88-7239-4fc9-8fa2-00889b44e10e")]
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
