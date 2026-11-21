using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace test.WinUI
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
            this.UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Write the full exception to a crash log next to the exe
            try
            {
                var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
                var msg = $"[{System.DateTime.Now:O}] {e.Exception?.GetType().FullName}: {e.Message}\n{e.Exception}\n\n";
                System.IO.File.AppendAllText(logPath, msg);
            }
            catch { }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
