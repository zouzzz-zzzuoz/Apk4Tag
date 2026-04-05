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
        const string LastBrowsedFolderKey = "LastBrowsedFolder";
        const string LastSearchTextKey = "LastFileBrowserSearch";
        const string SearchHistoryKey = "FileBrowserSearchHistory";
        const int MaxSearchHistoryItems = 5;

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
        Button _historyButton;
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
                SeparatorVisibility = SeparatorVisibility.Default,
                ItemTemplate = new DataTemplate(() =>
                {
                    var nameLabel = new Label
                    {
                        FontSize = 14,
                        VerticalOptions = LayoutOptions.Center,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        TextColor = Colors.Black
                    };
                    nameLabel.SetBinding(Label.TextProperty, nameof(FileItem.DisplayName));

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
                        InputTransparent = true,
                        BackgroundColor = Color.FromArgb("#F0F0F0")
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
                        ColumnSpacing = 10,
                        Padding = new Thickness(4, 6),
                        Children = { miniPreview, miniImage, textCol }
                    };
                    Grid.SetColumn(miniPreview, 0);
                    Grid.SetColumn(miniImage, 0);
                    Grid.SetColumn(textCol, 1);

                    var cell = new ViewCell { View = row };
                    cell.BindingContextChanged += async (s, ev) =>
                    {
                        if (cell.BindingContext is not FileItem fi)
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
                                return;
                            }

                            if (fi.PreviewLoading)
                            {
                                miniPreview.IsVisible = true;
                                return;
                            }

                            fi.PreviewLoading = true;
                            miniPreview.IsVisible = true;
                            miniPreview.Drawable = null;

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
                            fi.PreviewLoading = false;
                            if (cell.BindingContext == fi && model != null)
                            {
                                miniPreview.Drawable = new TagPreviewDrawable { Model = model };
                                miniPreview.IsVisible = true;
                                miniPreview.Invalidate();
                            }
                            else
                            {
                                miniPreview.Drawable = null;
                                miniPreview.IsVisible = false;
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
            // Open file on tap of the list item (no separate 'Ouvrir' button)
            _fileList.ItemTapped += async (s, ev) =>
            {
                if (ev.Item is FileItem fi && !fi.IsDirectory)
                {
                    // set result and close
                    _tcs.TrySetResult(fi.FullPath);
                    await Navigation.PopModalAsync();
                }
            };

            _searchBar = new SearchBar
            {
                Placeholder = "Rechercher...",
                FontSize = 13,
                HeightRequest = 40,
                Margin = new Thickness(4, 0),
                BackgroundColor = Color.FromArgb("#E0E0E0")
            };
            _searchBar.Text = Preferences.Get(LastSearchTextKey, string.Empty);
            _searchBar.TextChanged += OnSearchTextChanged;
            // history is shown via a dedicated button next to the search box
            _historyButton = new Button
            {
                Text = "Historique",
                FontSize = 13,
                Padding = new Thickness(8, 4),
                HorizontalOptions = LayoutOptions.End
            };
            _historyButton.Clicked += OnHistoryClicked;

            var topBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 4,
                Padding = new Thickness(4)
            };
            topBar.Add(_rootPicker, 0);
            topBar.Add(backButton, 1);
            topBar.Add(cancelButton, 2);

            _previewLabel = new Label { IsVisible = false };
            _previewView = new GraphicsView { IsVisible = false };
            _previewImage = new Image { IsVisible = false };
            _previewButton = new Button { IsVisible = false };

            var mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
                RowSpacing = 2
            };

            mainGrid.Add(topBar, 0, 0);
            mainGrid.Add(_pathLabel, 0, 1);
            // place search bar and history button on the same row
            var searchRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 4,
                Padding = new Thickness(4, 0)
            };
            searchRow.Add(_searchBar, 0);
            searchRow.Add(_historyButton, 1);
            mainGrid.Add(searchRow, 0, 2);
            mainGrid.Add(_fileList, 0, 3);

            Content = mainGrid;

            DetectRoots();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (_roots.Count == 0)
                DetectRoots();
            else
                ApplySearchFilter(_searchBar.Text);
        }

        void UpdatePreview(FileItem? item)
        {
            _selectedItem = item;
        }

        async void OnOpenClicked(object? s, EventArgs ev)
        {
            if (_selectedItem == null) return;
            _tcs.TrySetResult(_selectedItem.FullPath);
            await Navigation.PopModalAsync();
        }

        void DetectRoots()
        {
            _roots.Clear();
            _rootPicker.Items.Clear();

#if ANDROID
            try
            {
                var ctx = Android.App.Application.Context;
                var externalDirs = ctx.GetExternalFilesDirs(null);
                var sdPaths = new List<string>();
                var internalPaths = new List<string>();

                if (externalDirs != null)
                {
                    foreach (var dir in externalDirs)
                    {
                        if (dir == null) continue;
                        var full = dir.AbsolutePath;
                        var androidIdx = full.IndexOf("/Android/", StringComparison.OrdinalIgnoreCase);
                        if (androidIdx > 0)
                            full = full.Substring(0, androidIdx);

                        bool isRemovable = false;
                        try
                        {
                            isRemovable = !full.StartsWith("/storage/emulated", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }

                        if (isRemovable)
                            sdPaths.Add(full);
                        else
                            internalPaths.Add(full);
                    }
                }

                int sdIdx = 1;
                foreach (var p in sdPaths.Distinct())
                {
                    _roots.Add(new StorageRoot { Label = $"📂 Carte SD {(sdPaths.Count > 1 ? sdIdx.ToString() : "")}", Path = p });
                    sdIdx++;
                }
                foreach (var p in internalPaths.Distinct())
                {
                    _roots.Add(new StorageRoot { Label = "📱 Mémoire interne", Path = p });
                }

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

            if (_roots.Count == 0)
                return;

            var lastDir = Preferences.Get(LastBrowsedFolderKey, "");
            var selectedRootIndex = 0;
            var startPath = _roots[0].Path;

            if (!string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir))
            {
                for (int i = 0; i < _roots.Count; i++)
                {
                    if (lastDir.StartsWith(_roots[i].Path, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedRootIndex = i;
                        startPath = lastDir;
                        break;
                    }
                }
            }

            _rootPicker.SelectedIndex = selectedRootIndex;
            NavigateTo(startPath);
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
                    _tcs.TrySetResult(null);
                    await Navigation.PopModalAsync();
                    return;
                }

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
                Preferences.Set(LastBrowsedFolderKey, path);
                _selectedItem = null;
                _fileList.SelectedItem = null;

                var items = new List<FileItem>();

                // List subdirectories first so the user can navigate deeper
                try
                {
                    var dirs = Directory.GetDirectories(path)
                        .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
                    foreach (var d in dirs)
                    {
                        items.Add(new FileItem
                        {
                            FullPath = d,
                            DisplayName = "📁 " + Path.GetFileName(d),
                            IsDirectory = true,
                            Icon = "📁",
                            IsTagFile = false,
                            IsImageFile = false
                        });
                    }
                }
                catch { }

                // Then list supported files
                try
                {
                    var files = Directory.GetFiles(path)
                        .Where(f => _supportedExtensions.Contains(Path.GetExtension(f)))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        items.Add(new FileItem
                        {
                            FullPath = f,
                            DisplayName = Path.GetFileName(f),
                            IsDirectory = false,
                            Icon = ext == ".tag" ? "📐" : "🖼",
                            IsTagFile = ext == ".tag",
                            IsImageFile = ext != ".tag"
                        });
                    }
                }
                catch { }

                _allItems = items;
                ApplySearchFilter(_searchBar.Text);
            }
            catch (UnauthorizedAccessException)
            {
                _pathLabel.Text = "⚠ Accès refusé : " + path;
                _allItems = new List<FileItem>();
                _fileList.ItemsSource = _allItems;
            }
            catch (Exception ex)
            {
                _pathLabel.Text = "⚠ " + ex.Message;
                _allItems = new List<FileItem>();
                _fileList.ItemsSource = _allItems;
            }
        }

        async void OnFileSelected(object? sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is not FileItem item)
            {
                UpdatePreview(null);
                return;
            }

            // If the user tapped a directory, navigate into it
            if (item.IsDirectory)
            {
                _fileList.SelectedItem = null;
                NavigateTo(item.FullPath);
                return;
            }

            _selectedItem = item;
            UpdatePreview(item);
            await Task.CompletedTask;
        }

        void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            var query = e.NewTextValue?.Trim() ?? string.Empty;
            Preferences.Set(LastSearchTextKey, query);
            SaveSearchHistory(query);
            ApplySearchFilter(query);
        }
        async void OnSearchBarFocused(object? sender, FocusEventArgs e)
        {
            // kept for compatibility but history is now shown via the history button
            await Task.CompletedTask;
        }

        async void OnHistoryClicked(object? sender, EventArgs e)
        {
            try
            {
                var history = GetSearchHistory();
                if (history.Count == 0)
                    return;

                var items = history.Take(MaxSearchHistoryItems).ToArray();
                var picked = await DisplayActionSheet("Historique des recherches", "Annuler", null, items);
                if (!string.IsNullOrWhiteSpace(picked) && picked != "Annuler")
                    _searchBar.Text = picked;
            }
            catch { }
        }

        void ApplySearchFilter(string? query)
        {
            var normalized = query?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                _fileList.ItemsSource = _allItems;
                return;
            }

            _fileList.ItemsSource = _allItems
                .Where(fi => fi.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        void SaveSearchHistory(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            try
            {
                var history = GetSearchHistory();
                history.RemoveAll(x => string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
                history.Insert(0, query);
                if (history.Count > MaxSearchHistoryItems)
                    history = history.Take(MaxSearchHistoryItems).ToList();
                Preferences.Set(SearchHistoryKey, string.Join('|', history));
            }
            catch { }
        }

        List<string> GetSearchHistory()
        {
            try
            {
                var raw = Preferences.Get(SearchHistoryKey, string.Empty);
                if (string.IsNullOrWhiteSpace(raw))
                    return new List<string>();

                return raw
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
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
                        // Do not render text labels in thumbnails to avoid clutter (remove red texts)
                        break;
                }
            }

            canvas.RestoreState();

            // Do not draw axes marker in thumbnails (remove red/green/yellow markers)
        }
    }
}
