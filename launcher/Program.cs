// 直播小帮手 · 启动器（安装根目录里那个可双击运行的程序）
//
// 为什么需要它：.NET 自包含程序的加载器要求运行时与依赖和主程序同目录（原生 DLL 按文件名
// 从 exe 目录加载、托管程序集按 AppContext.BaseDirectory 探测），所以无法把约 700 个依赖
// 挪到别处而让主 exe 留在根目录。做法是把整套程序放进 app\，根目录只留这一个轻量启动器：
// 双击即启动、图标同程序、不驻留（启动完立刻退出，托盘/单实例/自启都由真正的 exe 负责）。
//
// 用 P/Invoke 调 MessageBoxW 而不是 WinForms：不引入额外框架，体积与 AOT 兼容性都更好。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BltLauncher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_ICONERROR = 0x10;

    [STAThread]
    private static int Main(string[] args)
    {
        var exeDir = AppContext.BaseDirectory;
        var target = Path.Combine(exeDir, "app", "BiLi_live_Tool.exe");

        if (!File.Exists(target))
        {
            MessageBoxW(IntPtr.Zero,
                "没有找到主程序：\n" + target + "\n\n" +
                "请确认 app 文件夹没有被删除或改名；若确实缺失，重新运行「直播小帮手」安装包修复即可。",
                "直播小帮手", MB_OK | MB_ICONWARNING);
            return 2;
        }

        try
        {
            var psi = new ProcessStartInfo(target)
            {
                WorkingDirectory = Path.GetDirectoryName(target)!,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, "启动失败：\n" + ex.Message, "直播小帮手", MB_OK | MB_ICONERROR);
            return 1;
        }
    }
}
