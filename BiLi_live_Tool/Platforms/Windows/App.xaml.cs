using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BiLi_live_Tool.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // 🚨 UI 线程与 WinUI 3 渲染管线未捕获异常终极护栏：
            // 捕获所有未处理的 XAML/UI 异常并强制落盘，同时设置 e.Handled = true，
            // 坚决阻止 WinUI 3 触发 Fast-Fail 导致整个主进程猝死闪退！
            this.UnhandledException += (sender, e) =>
            {
                try
                {
                    BiLi_live_Tool.Services.CrashTrap.RecordCrash("WinUI.UnhandledException", e.Exception, isTerminating: false);
                    e.Handled = true;
                }
                catch { }
            };
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
