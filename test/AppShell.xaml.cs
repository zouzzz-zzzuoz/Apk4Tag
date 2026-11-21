using System.Linq;
using Microsoft.Maui.Controls;

namespace test
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            // Populate recent previews in title view if any
            PopulateRecentPreviews();
        }
        private async void OnShellOpenClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//MainPage");
            (Shell.Current.CurrentPage as MainPage)?.OnOpenShell();
        }

        void PopulateRecentPreviews()
        {
            try
            {
                var stack = this.FindByName<Microsoft.Maui.Controls.HorizontalStackLayout>("RecentPreviewsStack");
                if (stack == null) return;

                var recent = Microsoft.Maui.Storage.Preferences.Get("RecentFiles", "");
                if (string.IsNullOrEmpty(recent)) return;

                var parts = recent.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts.Take(8))
                {
                    try
                    {
                        View previewView;
                        var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
                        if (ext == ".tag" || ext == ".dxf" || ext == ".dwg" || ext == ".pdf" || ext == ".xls" || ext == ".xlsx")
                        {
                            // show simple placeholder for tag files; tapping will re-open
                            previewView = new Frame
                            {
                                WidthRequest = 140,
                                HeightRequest = 88,
                                Padding = 4,
                                Content = new Label { Text = System.IO.Path.GetFileName(p), FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation }
                            };
                        }
                        else
                        {
                            var img = new Image { WidthRequest = 140, HeightRequest = 88, Aspect = Aspect.AspectFill };
                            try { img.Source = ImageSource.FromFile(p); }
                            catch { img.Source = null; }
                            previewView = new Frame { WidthRequest = 140, HeightRequest = 88, Padding = 2, Content = img };
                        }

                        var tap = new TapGestureRecognizer();
                        tap.Tapped += async (s, e) =>
                        {
                            try
                            {
                                await Shell.Current.GoToAsync("//MainPage");
                                if (Application.Current?.MainPage is AppShell shell && shell.CurrentPage is MainPage mp)
                                {
                                    await mp.OpenFileAsync(p);
                                }
                            }
                            catch { }
                        };
                        previewView.GestureRecognizers.Add(tap);
                        stack.Children.Add(previewView);
                    }
                    catch { }
                }
            }
            catch { }
        }
        // Reset button removed from shell title
        private void OnShellFitClicked(object sender, EventArgs e)
        {
            (Shell.Current.CurrentPage as MainPage)?.OnFitShell();
        }
        private void OnShellMeasureDoubleClicked(object sender, EventArgs e)
        {
            (Shell.Current.CurrentPage as MainPage)?.OnMeasureDoubleShell();
        }
        private void OnShellMeasureLinearClicked(object sender, EventArgs e)
        {
            (Shell.Current.CurrentPage as MainPage)?.OnMeasureLinearShell();
        }
        private void OnShellToggleDimensionsClicked(object sender, EventArgs e)
        {
            (Shell.Current.CurrentPage as MainPage)?.OnToggleDimensionsShell();
        }
        private void OnShellZoomOutClicked(object sender, EventArgs e)
        {
            (Shell.Current.CurrentPage as MainPage)?.OnZoomOutx2();
        }
    }
}
