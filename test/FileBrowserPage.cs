using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace test
{
    /// <summary>
    /// Custom file browser with .tag preview.
    /// Storage roots are ordered: SD card first, then internal memory.
    /// </summary>
    public class FileBrowserPage : ContentPage
    {
        readonly TaskCompletionSource<string?> _tcs;
        string _currentDir = "";
        readonly ListView _fileList;
        readonly Label _pathLabel;
        readonly SearchBar _searchBar;
        readonly Picker _rootPicker;
        readonly List<StorageRoot> _roots = new();
        List<FileItem> _allItems = new();
        FileItem? _selectedItem;
        GraphicsView _previewView;
        Image _previewImage;
        Label _previewLabel;
        Button _previewButton;
        // supported file extensions
        static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".tag", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
        };

        class StorageRoot
        {
            public string Label { get; set; } = "";
            public string Path { get; set; } = "";
            public override string ToString() => Label;
        }

        public FileBrowserPage(TaskCompletionSource<string?> tcs)
        {
            _tcs = tcs;
            Title = "Ouvrir un fichier";
            BackgroundColor = Colors.White;

            _rootPicker = new Picker
            {
                Title = "Emplacement",
                HorizontalOptions = LayoutOptions.Fill,
                FontSize = 14
            };
            _rootPicker.SelectedIndexChanged += OnRootChanged;

            _pathLabel = new Label
            {
                FontSize = 12,
                TextColor = Colors.Gray,
                LineBreakMode = LineBreakMode.TailTruncation,
                Margin = new Thickness(4, 2)
            };

            var backButton = new Button
            {
                Text = "⬆ Monter",
                FontSize = 13,
                Padding = new Thickness(8, 4),
                HorizontalOptions = LayoutOptions.Start
            };
            backButton.Clicked += OnBackClicked;

            var openButton = new Button
            {
                Text = "Ouvrir",
                FontSize = 13,
                Padding = new Thickness(8,4),
                HorizontalOptions = LayoutOptions.End
            };
            openButton.Clicked += OnOpenClicked;

            var cancelButton = new Button
            {
                Text = "Annuler",
                FontSize = 13,
                Padding = new Thickness(8, 4),
                HorizontalOptions = LayoutOptions.End,
                TextColor = Colors.Red
            };
            cancelButton.Clicked += OnCancelClicked;

            _fileList = new ListView
            {
                HasUnevenRows = true,
                ItemTemplate = new DataTemplate(() =>
                {
                    var nameLabel = new Label { FontSize = 14, VerticalOptions = LayoutOptions.Center };
                    nameLabel.SetBinding(Label.TextProperty, "DisplayName");

                    // Mini preview inside each row (GraphicsView for .tag, Image for images)
                    var miniPreview = new GraphicsView
                    {
                        HeightRequest = 110,
                        WidthRequest = 140,
                        VerticalOptions = LayoutOptions.Center,
                        Background = new SolidColorBrush(Color.FromArgb("#F0F0F0")),
                        IsVisible = false,
                        InputTransparent = true
                    };
                    var miniImage = new Image
                    {
                        HeightRequest = 110,
                        WidthRequest = 140,
                        VerticalOptions = LayoutOptions.Center,
                        Aspect = Aspect.AspectFit,
                        IsVisible = false,
                        InputTransparent = true
                    };

                    var textCol = new VerticalStackLayout
                    {
                        Spacing = 1,
                        VerticalOptions = LayoutOptions.Center,
                        Children = { nameLabel }
                    };

                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitionCollection
                        {
                            new ColumnDefinition(new GridLength(145)),
                            new ColumnDefinition(GridLength.Star)
                        },
                        ColumnSpacing = 6,
                        Padding = new Thickness(4, 6),
                        Children = { miniPreview, miniImage, textCol }
                    };
                    // small per-row preview button (fallback to native picker when direct file access fails)
                    var rowPreviewBtn = new Button
                    {
                        Text = "🔍",
                        FontSize = 12,
                        WidthRequest = 40,
                        HeightRequest = 36,
                        HorizontalOptions = LayoutOptions.End,
                        VerticalOptions = LayoutOptions.Center,
                        IsVisible = false
                    };
                    row.Children.Add(rowPreviewBtn);
                    Grid.SetColumn(rowPreviewBtn, 1);
                    Grid.SetColumn(miniPreview, 0);
                    Grid.SetColumn(miniImage, 0);
                    Grid.SetColumn(textCol, 1);

                    var cell = new ViewCell { View = row };

                    // Lazy-load mini preview when the cell scrolls into view
                    cell.BindingContextChanged += async (s, ev) =>
                    {
                        if (cell.BindingContext is not FileItem fi || fi.IsDirectory)
                        {
                            miniPreview.Drawable = null;
                            miniPreview.IsVisible = false;
                            miniImage.Source = null;
                            miniImage.IsVisible = false;
                            return;
                        }

                        if (fi.IsImageFile)
                        {
                            miniPreview.Drawable = null;
                            miniPreview.IsVisible = false;
                            try { miniImage.Source = ImageSource.FromFile(fi.FullPath); }
                            catch { miniImage.Source = null; }
                            miniImage.IsVisible = true;
                            return;
                        }

                        if (fi.IsTagFile)
                        {
                            miniImage.Source = null;
                            miniImage.IsVisible = false;

                            if (fi.PreviewModel != null)
                            {
                                miniPreview.Drawable = new TagPreviewDrawable { Model = fi.PreviewModel };
                                miniPreview.IsVisible = true;
                                miniPreview.Invalidate();
                                rowPreviewBtn.IsVisible = false;
                                return;
                            }

                            if (fi.PreviewLoading)
                            {
                                miniPreview.IsVisible = true;
                                rowPreviewBtn.IsVisible = false;
                                return;
                            }

                            fi.PreviewLoading = true;
                            miniPreview.IsVisible = true;
                            miniPreview.Drawable = null;
                            rowPreviewBtn.IsVisible = false;

                            var filePath = fi.FullPath;
                            TagModel? model = null;
                            try
                            {
                                model = await Task.Run(() =>
                                {
                                    try
                                    {
                                        using var stream = File.OpenRead(filePath);
                                        return TagParser.ParseLightweight(stream);
                                    }
                                    catch
                                    {
                                        return null;
                                    }
                                });
                            }
                            catch { model = null; }

                            fi.PreviewModel = model;
                            if (cell.BindingContext == fi && model != null)
                            {
                                miniPreview.Drawable = new TagPreviewDrawable { Model = model };
                                miniPreview.Invalidate();
                                rowPreviewBtn.IsVisible = false;
                            }
                            else
                            {
                                // If parsing failed (likely permission/content URI), show row preview button so user can pick via native picker
                                rowPreviewBtn.IsVisible = true;
                            }
                            return;
                        }

                        miniPreview.Drawable = null;
                        miniPreview.IsVisible = false;
                        miniImage.Source = null;
                        miniImage.IsVisible = false;
                    };

                    return cell;
                }),
                SelectionMode = ListViewSelectionMode.Single,
                VerticalOptions = LayoutOptions.FillAndExpand
            };
            _fileList.ItemSelected += OnFileSelected;

            // wire selection changed to update preview (UpdatePreview defined as class method)

            _searchBar = new SearchBar
            {
                Placeholder = "Rechercher...",
                FontSize = 13,
                HeightRequest = 40,
                Margin = new Thickness(4, 0),
                BackgroundColor = Color.FromArgb("#E0E0E0")
            };
            _searchBar.TextChanged += OnSearchTextChanged;

            var topBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 4,
                Padding = new Thickness(4)
            };
            topBar.Add(_rootPicker, 0);
            topBar.Add(backButton, 1);
            topBar.Add(openButton, 2);
            topBar.Add(cancelButton, 3);

            // Preview controls (created here so they can be referenced later)
            _previewLabel = new Label { FontSize = 14, TextColor = Colors.Black, LineBreakMode = LineBreakMode.TailTruncation };
            _previewView = new GraphicsView { HeightRequest = 300, WidthRequest = 300, Background = new SolidColorBrush(Color.FromArgb("#F8F8F8")), IsVisible = false };
            _previewImage = new Image { HeightRequest = 300, WidthRequest = 300, Aspect = Aspect.AspectFit, IsVisible = false };
            _previewButton = new Button { Text = "Prévisualiser (sélecteur natif)", IsVisible = false, FontSize = 12 };
            _previewButton.Clicked += async (s, ev) =>
            {
                try
                {
                    // Ask user to pick the same file via native picker; copy to cache and parse for preview
                    var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Sélectionnez le même fichier pour prévisualiser" });
                    if (result == null) return;
                    var dest = Path.Combine(FileSystem.CacheDirectory, result.FileName ?? Guid.NewGuid().ToString());
                    using (var src = await result.OpenReadAsync())
                    using (var dst = File.Create(dest))
                    {
                        await src.CopyToAsync(dst);
                    }

                    // parse lightweight and update preview
                    TagModel? model = null;
                    try
                    {
                        using var fs = File.OpenRead(dest);
                        model = TagParser.ParseLightweight(fs);
                    }
                    catch { }

                    if (model != null && _selectedItem != null)
                    {
                        _selectedItem.PreviewModel = model;
                        _selectedItem.PreviewLoading = false;
                        _previewButton.IsVisible = false;
                        _previewImage.IsVisible = false;
                        _previewView.Drawable = new TagPreviewDrawable { Model = model };
                        _previewView.IsVisible = true;
                        _previewView.Invalidate();
                    }
                }
                catch { }
            };

            // Two-column view: file list (left) and preview (right)
            var mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(GridLength.Auto),    // root picker + buttons
                    new RowDefinition(GridLength.Auto),    // path
                    new RowDefinition(GridLength.Auto),    // search bar
                    new RowDefinition(GridLength.Star),    // content area
                },
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(320))
                },
                RowSpacing = 2,
                ColumnSpacing = 6
            };

            // Left side controls
            mainGrid.Add(topBar, 0, 0);
            Grid.SetColumnSpan(topBar, 2);
            mainGrid.Add(_pathLabel, 0, 1);
            Grid.SetColumnSpan(_pathLabel, 2);
            mainGrid.Add(_searchBar, 0, 2);
            Grid.SetColumnSpan(_searchBar, 2);

            // File list on the left
            mainGrid.Add(_fileList, 0, 3);

            // Preview stack on the right
            var previewStack = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(6) };
            previewStack.Children.Add(_previewLabel);
            previewStack.Children.Add(_previewView);
            previewStack.Children.Add(_previewImage);
            previewStack.Children.Add(_previewButton);
            mainGrid.Add(previewStack, 1, 3);

            Content = mainGrid;

            DetectRoots();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Re-check permissions and refresh roots when returning from settings
            if (_roots.Count == 0)
                DetectRoots();
        }

        void UpdatePreview(FileItem? item)
        {
            try
            {
                _selectedItem = item;
                if (item == null)
                {
                    if (_previewLabel != null) _previewLabel.Text = string.Empty;
                    if (_previewView != null) { _previewView.Drawable = null; _previewView.IsVisible = false; }
                    if (_previewImage != null) { _previewImage.Source = null; _previewImage.IsVisible = false; }
                    return;
                }

                if (_previewLabel != null) _previewLabel.Text = item.DisplayName;

                if (item.IsImageFile)
                {
                    if (_previewView != null) { _previewView.Drawable = null; _previewView.IsVisible = false; }
                    if (_previewImage != null)
                    {
                        try { _previewImage.Source = ImageSource.FromFile(item.FullPath); }
                        catch { _previewImage.Source = null; }
                        _previewImage.IsVisible = true;
                    }
                    return;
                }

                if (item.IsTagFile)
                {
                    if (_previewImage != null) { _previewImage.Source = null; _previewImage.IsVisible = false; }

                    if (item.PreviewModel != null)
                    {
                        if (_previewView != null) { _previewView.Drawable = new TagPreviewDrawable { Model = item.PreviewModel }; _previewView.IsVisible = true; _previewView.Invalidate(); }
                        if (_previewButton != null) _previewButton.IsVisible = false;
                        return;
                    }

                    if (!item.PreviewLoading)
                    {
                        item.PreviewLoading = true;
                        if (_previewView != null) { _previewView.IsVisible = true; _previewView.Drawable = null; }
                        _ = Task.Run(() =>
                        {
                            TagModel? model = null;
                            try
                            {
                                using var s = File.OpenRead(item.FullPath);
                                model = TagParser.ParseLightweight(s);
                            }
                            catch { }
                            item.PreviewModel = model;
                            item.PreviewLoading = false;
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                if (_selectedItem == item && model != null && _previewView != null)
                                {
                                    _previewView.Drawable = new TagPreviewDrawable { Model = model };
                                    _previewView.Invalidate();
                                    if (_previewButton != null) _previewButton.IsVisible = false;
                                }
                                else
                                {
                                    // If parsing failed (likely due to content URI / permission), show button to use native picker
                                    if (_previewButton != null)
                                    {
                                        _previewButton.IsVisible = true;
                                    }
                                }
                            });
                        });
                    }
                    return;
                }

                if (_previewView != null) { _previewView.Drawable = null; _previewView.IsVisible = false; }
                if (_previewImage != null) { _previewImage.Source = null; _previewImage.IsVisible = false; }
            }
            catch { }
        }

        async void OnOpenClicked(object? s, EventArgs ev)
        {
            if (_selectedItem == null) return;
            if (_selectedItem.IsDirectory)
            {
                NavigateTo(_selectedItem.FullPath);
                return;
            }
            _tcs.TrySetResult(_selectedItem.FullPath);
            await Navigation.PopModalAsync();
        }

        void DetectRoots()
        {
            _roots.Clear();

#if ANDROID
            try
            {
                // Get all external storage directories (SD card + internal emulated)
                var ctx = Android.App.Application.Context;
                var externalDirs = ctx.GetExternalFilesDirs(null);
                // Collect unique root paths, preferring SD card first
                var sdPaths = new List<string>();
                var internalPaths = new List<string>();

                if (externalDirs != null)
                {
                    foreach (var dir in externalDirs)
                    {
                        if (dir == null) continue;
                        // The path looks like /storage/XXXX-XXXX/Android/data/pkg/files
                        // We want the root: /storage/XXXX-XXXX
                        var full = dir.AbsolutePath;
                        var androidIdx = full.IndexOf("/Android/", StringComparison.OrdinalIgnoreCase);
                        if (androidIdx > 0)
                            full = full.Substring(0, androidIdx);

                        bool isRemovable = false;
                        try
                        {
                            // Internal emulated storage is under /storage/emulated/; anything else is SD card
                            isRemovable = !full.StartsWith("/storage/emulated", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }

                        if (isRemovable)
                            sdPaths.Add(full);
                        else
                            internalPaths.Add(full);
                    }
                }

                // SD card first
                int sdIdx = 1;
                foreach (var p in sdPaths.Distinct())
                {
                    _roots.Add(new StorageRoot { Label = $"📂 Carte SD {(sdPaths.Count > 1 ? sdIdx.ToString() : "")}", Path = p });
                    sdIdx++;
                }
                // Internal second
                foreach (var p in internalPaths.Distinct())
                {
                    _roots.Add(new StorageRoot { Label = "📱 Mémoire interne", Path = p });
                }

                // Fallback: common Android internal storage path
                if (_roots.Count == 0)
                {
                    var envPath = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
                    if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
                        _roots.Add(new StorageRoot { Label = "📱 Mémoire interne", Path = envPath });
                }
            }
            catch { }
#endif

#if WINDOWS
            try
            {
                // On Windows, list all available drives; removable drives (SD/USB) first
                var removable = new List<StorageRoot>();
                var fixedDrives = new List<StorageRoot>();
                foreach (var drv in DriveInfo.GetDrives())
                {
                    if (!drv.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(drv.VolumeLabel) ? drv.Name : $"{drv.VolumeLabel} ({drv.Name.TrimEnd('\\')})";
                    if (drv.DriveType == DriveType.Removable)
                        removable.Add(new StorageRoot { Label = "📂 " + label, Path = drv.RootDirectory.FullName });
                    else if (drv.DriveType == DriveType.Fixed)
                        fixedDrives.Add(new StorageRoot { Label = "💻 " + label, Path = drv.RootDirectory.FullName });
                }
                _roots.AddRange(removable);
                _roots.AddRange(fixedDrives);
            }
            catch { }
#endif

            // Absolute fallback
            if (_roots.Count == 0)
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                    _roots.Add(new StorageRoot { Label = "📁 Accueil", Path = home });
                else
                    _roots.Add(new StorageRoot { Label = "📁 Racine", Path = "/" });
            }

            foreach (var r in _roots)
                _rootPicker.Items.Add(r.ToString());

            if (_roots.Count > 0)
            {
                var lastDir = Preferences.Get("LastBrowsedFolder", "");
                if (!string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir))
                {
                    // Select the matching root in the picker
                    for (int i = 0; i < _roots.Count; i++)
                    {
                        if (lastDir.StartsWith(_roots[i].Path, StringComparison.OrdinalIgnoreCase))
                        {
                            _rootPicker.SelectedIndex = i;
                            break;
                        }
                    }
                    if (_rootPicker.SelectedIndex < 0)
                        _rootPicker.SelectedIndex = 0;
                    NavigateTo(lastDir);
                }
                else
                {
                    _rootPicker.SelectedIndex = 0;
                    NavigateTo(_roots[0].Path);
                }
            }
        }

        void OnRootChanged(object? sender, EventArgs e)
        {
            if (_rootPicker.SelectedIndex >= 0 && _rootPicker.SelectedIndex < _roots.Count)
                NavigateTo(_roots[_rootPicker.SelectedIndex].Path);
        }

#if ANDROID
        async Task TryOpenNativePickerAsync()
        {
            try
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Ouvrir un fichier"
                });

                if (result == null)
                {
                    // user cancelled
                    _tcs.TrySetResult(null);
                    await Navigation.PopModalAsync();
                    return;
                }

                // Copy picked content to app cache because on Android FileResult may be a content URI
                var dest = Path.Combine(FileSystem.CacheDirectory, result.FileName ?? Guid.NewGuid().ToString());
                using (var src = await result.OpenReadAsync())
                using (var dst = File.Create(dest))
                {
                    await src.CopyToAsync(dst);
                }

                _tcs.TrySetResult(dest);
                await Navigation.PopModalAsync();
            }
            catch
            {
                // ignore and keep the in-app browser as fallback
            }
        }
#endif

        void OnBackClicked(object? sender, EventArgs e)
        {
            try
            {
                var parent = Directory.GetParent(_currentDir)?.FullName;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    NavigateTo(parent);
            }
            catch { }
        }

        async void OnCancelClicked(object? sender, EventArgs e)
        {
            _tcs.TrySetResult(null);
            await Navigation.PopModalAsync();
        }

        void NavigateTo(string path)
        {
            try
            {
                _currentDir = path;
                _pathLabel.Text = path;
                Preferences.Set("LastBrowsedFolder", path);

                var items = new List<FileItem>();

                // Directories first (sorted)
                try
                {
                    var dirs = Directory.GetDirectories(path)
                        .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
                    foreach (var d in dirs)
                    {
                        var name = Path.GetFileName(d);
                        if (name.StartsWith(".")) continue; // hide hidden dirs
                        items.Add(new FileItem { FullPath = d, DisplayName = name, IsDirectory = true, Icon = "📁" });
                    }
                }
                catch { }

                // Files (only supported extensions, sorted)
                try
                {
                    var files = Directory.GetFiles(path)
                        .Where(f => _supportedExtensions.Contains(Path.GetExtension(f)))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        string icon = ext == ".tag" ? "📐" : "🖼";

                        items.Add(new FileItem
                        {
                            FullPath = f,
                            DisplayName = Path.GetFileName(f),
                            IsDirectory = false,
                            Icon = icon,
                            IsTagFile = (ext == ".tag"),
                            IsImageFile = (ext != ".tag")
                        });
                    }
                }
                catch { }

                _fileList.ItemsSource = items;
                _allItems = items;
                _searchBar.Text = string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                _pathLabel.Text = "⚠ Accès refusé : " + path;
            }
            catch (Exception ex)
            {
                _pathLabel.Text = "⚠ " + ex.Message;
            }
        }

        async void OnFileSelected(object? sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is not FileItem item)
            {
                UpdatePreview(null);
                return;
            }
            _fileList.SelectedItem = item; // keep visual selection

            if (item.IsDirectory)
            {
                NavigateTo(item.FullPath);
                return;
            }

            // Update preview pane instead of immediately opening
            UpdatePreview(item);
        }

        void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            var query = e.NewTextValue?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                _fileList.ItemsSource = _allItems;
                return;
            }
            _fileList.ItemsSource = _allItems
                .Where(fi => fi.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        class FileItem
        {
            public string FullPath { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool IsDirectory { get; set; }
            public string Icon { get; set; } = "";
            public bool IsTagFile { get; set; }
            public bool IsImageFile { get; set; }
            public TagModel? PreviewModel { get; set; }
            public bool PreviewLoading { get; set; }
            public bool HasPreview => IsTagFile || IsImageFile;
        }
    }

    /// <summary>
    /// Lightweight drawable that renders a TagModel as a small preview (miniature).
    /// Draws all geometry in black — no layer/dimension lookups for speed.
    /// </summary>
    class TagPreviewDrawable : IDrawable
    {
        public TagModel? Model { get; set; }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Color.FromArgb("#F5F5F5");
            canvas.FillRectangle(dirtyRect);
            if (Model == null) return;

            var width = Model.MaxX - Model.MinX;
            var height = Model.MaxY - Model.MinY;
            if (width <= 0) width = 1;
            if (height <= 0) height = 1;

            float sx = dirtyRect.Width / (float)width;
            float sy = dirtyRect.Height / (float)height;
            float s = Math.Min(sx, sy) * 0.85f;

            // Center the model and apply scale (flip Y to match model coordinates)
            canvas.SaveState();
            canvas.Translate(dirtyRect.Center.X, dirtyRect.Center.Y);
            canvas.Scale(s, -s);
            canvas.Translate((float)(-(Model.MinX + Model.MaxX) / 2.0), (float)(-(Model.MinY + Model.MaxY) / 2.0));

            // Stroke size in model coordinates chosen so the rendered stroke is ~1 device pixel minimum
            var strokeModel = 1f / s;
            strokeModel = Math.Clamp(strokeModel, 0.2f, 3f);
            canvas.StrokeSize = strokeModel;

            // Prebuild layer dict for colors/visibility
            var layerDict = Model.LayerDict;

            foreach (var ent in Model.Entities)
            {
                bool visible = true;
                Color entColor = Colors.Black;
                if (layerDict != null && layerDict.TryGetValue(ent.LayerId, out var lay))
                {
                    visible = lay.Visible;
                    entColor = lay.Color;
                    if (entColor.Red >= 0.95f && entColor.Green >= 0.95f && entColor.Blue >= 0.95f)
                        entColor = Colors.Black;
                }
                if (!visible) continue;

                switch (ent)
                {
                    case TagLine l:
                        canvas.StrokeColor = entColor;
                        canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2);
                        break;
                    case TagCircle c:
                        canvas.StrokeColor = entColor;
                        canvas.DrawCircle(c.CX, c.CY, c.R);
                        break;
                    case TagArc a:
                        canvas.StrokeColor = entColor;
                        double startRad = a.StartAngle * Math.PI / 180.0;
                        double sweepRad = a.SweepAngle * Math.PI / 180.0;
                        sweepRad = ((sweepRad % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                        if (sweepRad <= 0) sweepRad += 2 * Math.PI;
                        int segs = Math.Max(6, (int)Math.Ceiling(Math.Abs(sweepRad) / (2 * Math.PI) * 64));
                        double step = sweepRad / segs;
                        var arcPath = new PathF();
                        arcPath.MoveTo(a.CX + a.R * (float)Math.Cos(startRad), a.CY + a.R * (float)Math.Sin(startRad));
                        for (int i = 1; i <= segs; i++)
                        {
                            double ang = startRad + step * i;
                            arcPath.LineTo(a.CX + a.R * (float)Math.Cos(ang), a.CY + a.R * (float)Math.Sin(ang));
                        }
                        canvas.DrawPath(arcPath);
                        break;
                    case TagPoint p:
                        canvas.FillColor = entColor;
                        float pr = Math.Clamp(2f / s, 0.5f, 6f);
                        canvas.FillCircle(p.X, p.Y, pr);
                        break;
                    case TagText t:
                        try
                        {
                            canvas.SaveState();
                            canvas.Translate(t.X, t.Y);
                            canvas.Scale(1, -1);
                            canvas.FontSize = t.Size;
                            canvas.FontColor = Colors.Red;
                            canvas.DrawString(t.Text ?? string.Empty, 0, 0, HorizontalAlignment.Left);
                            canvas.RestoreState();
                        }
                        catch { }
                        break;
                }
            }

            canvas.RestoreState();

            // Draw small axes marker anchored top-right of the preview (screen coords)
            try
            {
                float margin = Math.Min(12f, dirtyRect.Width * 0.06f);
                float axisLen = Math.Max(10f, Math.Min(dirtyRect.Width, dirtyRect.Height) * 0.12f);
                float ox = dirtyRect.Right - margin;
                float oy = dirtyRect.Top + margin;

                canvas.SaveState();
                canvas.StrokeSize = 2f;
                canvas.StrokeColor = Colors.Red;
                canvas.DrawLine(ox, oy, ox - axisLen, oy);
                canvas.StrokeColor = Colors.Green;
                canvas.DrawLine(ox, oy, ox, oy + axisLen);
                canvas.FillColor = Colors.Yellow;
                canvas.FillCircle(ox, oy, Math.Max(3f, axisLen * 0.08f));
                canvas.RestoreState();
            }
            catch { }
        }
    }
}
