using Microsoft.Extensions.DependencyInjection;

namespace test
{
    public partial class App : Application
    {
        /// <summary>
        /// Stores a file path received via an Android VIEW intent during cold start.
        /// MainPage.OnAppearing consumes this once the UI is ready.
        /// </summary>
        public static string? PendingOpenFilePath { get; set; }

        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}