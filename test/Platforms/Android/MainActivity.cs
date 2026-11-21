using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Android.Views;
using System.IO;

namespace test
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        LaunchMode = LaunchMode.SingleTop,
        Exported = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    // .tag files via file://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "*/*", DataPathPattern = ".*\\.tag")]
    // .tag files via content://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "*/*", DataPathPattern = ".*\\.tag")]
    // .tag files via content:// with application/octet-stream (some file managers)
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "application/octet-stream")]
    // Images via file://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "image/*")]
    // Images via content://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "image/*")]
    // .dxf files via file://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "*/*", DataPathPattern = ".*\\.dxf")]
    // .dxf files via content://
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "*/*", DataPathPattern = ".*\\.dxf")]
    public class MainActivity : MauiAppCompatActivity
    {
        // Native pinch-to-zoom via ScaleGestureDetector
        ScaleGestureDetector? _scaleDetector;

        /// <summary>
        /// Static event fired on the main thread during pinch gestures.
        /// Parameters: scaleFactor (incremental), focusX (screen px), focusY (screen px), status (0=begin, 1=scale, 2=end).
        /// </summary>
        public static event Action<float, float, float, int>? PinchEvent;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Resolve the VIEW intent BEFORE MAUI bootstraps.
            try { HandleViewIntent(Intent); } catch { }

            base.OnCreate(savedInstanceState);

            _scaleDetector = new ScaleGestureDetector(this, new PinchScaleListener());
            // Disable quick-scale (double-tap-drag) to avoid interfering with double-tap zoom
            _scaleDetector.QuickScaleEnabled = false;
        }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e != null)
                _scaleDetector?.OnTouchEvent(e);
            return base.DispatchTouchEvent(e);
        }

        sealed class PinchScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
        {
            public override bool OnScaleBegin(ScaleGestureDetector? detector)
            {
                if (detector != null)
                    PinchEvent?.Invoke(1f, detector.FocusX, detector.FocusY, 0);
                return true;
            }

            public override bool OnScale(ScaleGestureDetector? detector)
            {
                if (detector != null)
                    PinchEvent?.Invoke(detector.ScaleFactor, detector.FocusX, detector.FocusY, 1);
                return true;
            }

            public override void OnScaleEnd(ScaleGestureDetector? detector)
            {
                PinchEvent?.Invoke(1f, 0f, 0f, 2);
            }
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            try { HandleViewIntent(intent); } catch { }
        }

        void HandleViewIntent(Intent? intent)
        {
            if (intent?.Action != Intent.ActionView) return;
            var data = intent.Data;
            if (data == null) return;

            string? filePath = null;
            try
            {
                if (data.Scheme == "file")
                {
                    filePath = data.Path;
                }
                else if (data.Scheme == "content")
                {
                    // Get display name (real filename) from content provider
                    string? displayName = null;
                    try
                    {
                        using var cursor = ContentResolver?.Query(data, null, null, null, null);
                        if (cursor != null && cursor.MoveToFirst())
                        {
                            int idx = cursor.GetColumnIndex("_display_name");
                            if (idx >= 0)
                                displayName = cursor.GetString(idx);
                        }
                    }
                    catch { }

                    // Fallback: use last path segment
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = data.LastPathSegment ?? "file";
                        try
                        {
                            var mime = ContentResolver?.GetType(data);
                            if (!string.IsNullOrEmpty(mime))
                            {
                                var ext = MimeTypeMap.Singleton?.GetExtensionFromMimeType(mime);
                                if (!string.IsNullOrEmpty(ext) && !displayName.Contains('.'))
                                    displayName += "." + ext;
                            }
                        }
                        catch { }
                    }

                    // Sanitize filename (remove path separators and invalid chars)
                    displayName = Path.GetFileName(displayName);
                    foreach (var c in Path.GetInvalidFileNameChars())
                        displayName = displayName.Replace(c, '_');

                    // Copy content stream to app cache so OpenFileAsync can read it
                    try
                    {
                        using var input = ContentResolver?.OpenInputStream(data);
                        if (input != null)
                        {
                            var cacheDir = CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
                            var tempPath = Path.Combine(cacheDir, displayName);
                            using (var output = File.Create(tempPath))
                            {
                                input.CopyTo(output);
                            }
                            filePath = tempPath;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            // Validate supported file extension
            var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedExts = new[] { ".tag", ".dxf", ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
            if (!supportedExts.Contains(fileExt)) return;

            // Store for cold start (MainPage.OnAppearing will pick it up if MainPage isn't ready yet)
            App.PendingOpenFilePath = filePath;

            // Try to open immediately (works for warm start / OnNewIntent when MainPage already exists).
            // Wrapped in try-catch because MainThread API may not be available before MAUI bootstrap.
            try
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        // Retry briefly in case MainPage isn't navigated to yet
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            var main = (Microsoft.Maui.Controls.Application.Current?.MainPage as AppShell)?.CurrentPage as MainPage;
                            if (main != null)
                            {
                                App.PendingOpenFilePath = null;
                                await main.OpenFileAsync(filePath);
                                return;
                            }
                            await Task.Delay(300);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }
    }
}
