using Android.App;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;

namespace test.Platforms.Android
{
    // Simple non-blocking splash activity: rely on the themed launch drawable and
    // wait a short time without blocking the UI thread before launching MainActivity.
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, NoHistory = true, Exported = true)]
    public class SplashActivity : Activity
    {
        protected override async void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Wait briefly so the system-rendered launch drawable is visible. This does not
            // block the UI thread because we use Task.Delay and await.
            await Task.Delay(350);

            // Start the MAUI MainActivity
            var intent = new Intent(this, typeof(MainActivity));
            StartActivity(intent);
            // Finish the splash so it's not kept in the back stack
            Finish();
        }
    }
}
