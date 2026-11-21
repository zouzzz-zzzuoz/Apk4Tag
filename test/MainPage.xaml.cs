using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Graphics;
using Microsoft.Maui;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace test
{
    public partial class MainPage : ContentPage
    {
        TagModel? _model;
        TagDrawable _drawable = new TagDrawable();
        // image transforms
        float _imgStartTransX = 0f;
        float _imgStartTransY = 0f;
        // debounce single-tap so double-tap zoom does not alter snap points
        CancellationTokenSource? _singleTapCts;
        // removed sample counter control references

        public MainPage()
        {
            InitializeComponent();
            VersionLabel.Text = BuildInfo.Version;
            Viewer.Drawable = _drawable;
            // attach gestures programmatically
            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPanUpdated;
            Viewer.GestureRecognizers.Add(pan);
            var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            doubleTap.Tapped += OnDoubleTapped;
            Viewer.GestureRecognizers.Add(doubleTap);
            // Attach separate recognizers for the image viewer so gestures work on images
            var imagePan = new PanGestureRecognizer();
            imagePan.PanUpdated += OnImagePanUpdated;
            ViewerImage.GestureRecognizers.Add(imagePan);

            var imageDoubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            imageDoubleTap.Tapped += OnDoubleTapped;
            ViewerImage.GestureRecognizers.Add(imageDoubleTap);
            // Single tap for measurements
            var singleTap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
            singleTap.Tapped += OnSingleTapped;
            Viewer.GestureRecognizers.Add(singleTap);
            var imageSingleTap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
            imageSingleTap.Tapped += OnSingleTapped;
            ViewerImage.GestureRecognizers.Add(imageSingleTap);
            // Pinch-to-zoom is handled natively on Android via MainActivity.ScaleGestureDetector
            // Default shell buttons: no file opened -> only Open active
            SetShellButtonsMode(ShellButtonsMode.None);
        }

        void OnCompanyLinkTapped(object sender, EventArgs e)
        {
            try
            {
                var uri = new Uri("https://www.steco-industries.fr/odoo");
                Browser.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
            }
            catch { }
        }

        void HideKeyboard()
        {
            try
            {
#if ANDROID
                var activity = Platform.CurrentActivity;
                var imm = activity?.GetSystemService(Android.Content.Context.InputMethodService) as Android.Views.InputMethods.InputMethodManager;
                var token = activity?.Window?.DecorView?.WindowToken;
                if (imm != null && token != null)
                    imm.HideSoftInputFromWindow(token, 0);
#elif IOS || MACCATALYST
                // iOS/MacCatalyst: unfocus any focused element
                this.Dispatcher.Dispatch(() =>
                {
                    var current = this.FindByName<View>("_searchBar");
                    current?.Unfocus();
                });
#else
                // fallback: unfocus page
                this.Dispatcher.Dispatch(() => this.Unfocus());
#endif
            }
            catch { }
        }

        // Activate manual reselection mode for the specified measure point
        public void OnCycleSnapPoint1Clicked(object sender, EventArgs e)
        {
            ActivateReselectMode(1);
        }

        public void OnCycleSnapPoint2Clicked(object sender, EventArgs e)
        {
            ActivateReselectMode(2);
        }

        void ActivateReselectMode(int pointIndex)
        {
            // Toggle: if already reselecting this point, cancel
            if (_reselectingPoint == pointIndex)
            {
                _reselectingPoint = 0;
            }
            else
            {
                _reselectingPoint = pointIndex;
            }
            UpdateReselectButtonColors();
        }

        void UpdateReselectButtonColors()
        {
            if (_reselectingPoint == 1)
                CycleSnapPoint1Button.BackgroundColor = Colors.Orange;
            else
                CycleSnapPoint1Button.BackgroundColor = Colors.Brown;

            if (_reselectingPoint == 2)
                CycleSnapPoint2Button.BackgroundColor = Colors.Orange;
            else
                CycleSnapPoint2Button.BackgroundColor = Colors.Gray;
        }

        void CycleSnapForPoint(int pointIndex)
        {
            try
            {
                // choose base point depending on index; if the selected measure point is not set, do nothing
                PointF? basePtNullable = pointIndex == 1 ? _drawable.MeasurePoint1 : _drawable.MeasurePoint2;
                if (!basePtNullable.HasValue) return;
                PointF basePt = basePtNullable.Value;
                var vw = (float)Viewer.Width;
                var vh = (float)Viewer.Height;
                var modelWidth = (_model?.MaxX - _model?.MinX) ?? 1;
                var modelHeight = (_model?.MaxY - _model?.MinY) ?? 1;
                if (modelWidth <= 0) modelWidth = 1;
                if (modelHeight <= 0) modelHeight = 1;
                float fitSx = vw / (float)modelWidth;
                float fitSy = vh / (float)modelHeight;
                float fitS = Math.Min(fitSx, fitSy) * 0.9f;

                var candidates = GetSnapCandidates(basePt.X, basePt.Y, fitS * _drawable.Scale);
                if (candidates.Count == 0) return;

                // advance candidate index for the selected point (keep separate indices could be added; reuse shared index)
                _cycleCandidateIndex = (_cycleCandidateIndex + 1) % candidates.Count;
                var chosen = candidates[_cycleCandidateIndex];

                if (pointIndex == 1)
                    _drawable.MeasurePoint1 = chosen;
                else
                    _drawable.MeasurePoint2 = chosen;

                Viewer.Invalidate();
            }
            catch { }
        }

        // radius detection handled on the drawable instance
        // shell callbacks
        public void OnOpenShell() => OnOpenTagClicked(this, EventArgs.Empty);
        public void OnResetShell() => OnResetClicked(this, EventArgs.Empty);
        public void OnFitShell() => OnFitClicked(this, EventArgs.Empty);
        public void OnMeasureDoubleShell() => OnMeasureDoubleClicked(this, EventArgs.Empty);
        public void OnMeasureLinearShell() => OnMeasureLinearClicked(this, EventArgs.Empty);
        public void OnToggleDimensionsShell() => OnToggleDimensionsClicked(this, EventArgs.Empty);

        private void OnZoomOutx2Clicked(object sender, EventArgs e) => OnZoomOutx2();

        public void OnZoomOutx2()
        {
            // Image zoom-out when viewing an image (no .tag model)
            if (_model == null)
            {
                if (!ViewerImage.IsVisible) return;
                float newImgScale = _imgScale / 2f;
                if (newImgScale < 0.1f) newImgScale = 0.1f;
                float ratio = newImgScale / _imgScale;
                _imgPanX *= ratio;
                _imgPanY *= ratio;
                _imgScale = newImgScale;
                ViewerImage.Scale = _imgScale;
                ViewerImage.TranslationX = _imgPanX;
                ViewerImage.TranslationY = _imgPanY;
                return;
            }
            var vw = (float)Viewer.Width;
            var vh = (float)Viewer.Height;
            if (vw <= 0 || vh <= 0) return;

            float centerX = vw / 2f;
            float centerY = vh / 2f;

            float oldScale = _drawable.Scale;
            float newScale = oldScale / 2f;
            if (newScale < 0.1f) newScale = 0.1f;

            // Adjust pan so the center of the screen stays fixed
            double modelCenterX = (_model.MinX + _model.MaxX) / 2.0;
            double modelCenterY = (_model.MinY + _model.MaxY) / 2.0;
            var modelWidth = _model.MaxX - _model.MinX;
            var modelHeight = _model.MaxY - _model.MinY;
            if (modelWidth <= 0) modelWidth = 1;
            if (modelHeight <= 0) modelHeight = 1;
            float fitSx = vw / (float)modelWidth;
            float fitSy = vh / (float)modelHeight;
            float fitS = Math.Min(fitSx, fitSy) * 0.9f;

            double screenModelX = modelCenterX + (centerX - (centerX + _drawable.PanX)) / (fitS * oldScale);
            double screenModelY = modelCenterY - (centerY - (centerY + _drawable.PanY)) / (fitS * oldScale);

            double newPanX = _drawable.PanX + fitS * (oldScale - newScale) * (screenModelX - modelCenterX);
            double newPanY = _drawable.PanY + fitS * (newScale - oldScale) * (screenModelY - modelCenterY);

            _drawable.Scale = newScale;
            _drawable.PanX = (float)newPanX;
            _drawable.PanY = (float)newPanY;

            Viewer.Invalidate();
        }

        // image view transform state
        float _imgScale = 1f;
        float _imgPanX = 0f;
        float _imgPanY = 0f;
        // flag to block pan gesture during pinch
        bool _isPinching = false;
        float _panStartPanX = 0f;
        float _panStartPanY = 0f;
        // sensitivity for finger pan (0..1). Lower = slower movement.
        const float PanSensitivity = 0.6f; // 2x plus rapide (était 0.3f)
        // cycling snap candidates state
        List<PointF> _cycleCandidates = new List<PointF>();
        int _cycleCandidateIndex = -1;
        // manual reselection mode: 0 = none, 1 = reselecting P1, 2 = reselecting P2
        int _reselectingPoint = 0;
        // pinch sensitivity exponent default (used for double-tap zoom)
        double PinchPower = 20.0;

        protected override async void OnAppearing()
        {
            base.OnAppearing();
#if ANDROID
            MainActivity.PinchEvent += OnNativePinch;
#endif
            UpdateMeasurementUI();
            bool show = _drawable.MeasurementMode != MeasurementType.None && _model != null;
            CycleSnapPoint1Button.IsVisible = show;
            CycleSnapPoint1Button.IsEnabled = show && _drawable.MeasurePoint1.HasValue;
            CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
            CycleSnapPoint2Button.IsVisible = show;
            CycleSnapPoint2Button.IsEnabled = show && _drawable.MeasurePoint2.HasValue;
            CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;

            // Check for pending file from an external intent (cold start scenario)
            await TryOpenPendingFileAsync();

            // Safety net: if HandleViewIntent runs AFTER OnAppearing (race condition),
            // re-check after a short delay.
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                Dispatcher.Dispatch(async () =>
                {
                    try { await TryOpenPendingFileAsync(); } catch { }
                });
            });
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
#if ANDROID
            MainActivity.PinchEvent -= OnNativePinch;
#endif
        }

        #if ANDROID
        // Native Android pinch handler — called from MainActivity.ScaleGestureDetector
        void OnNativePinch(float scaleFactor, float screenFocalX, float screenFocalY, int status)
        {
            try
            {
                // status: 0 = begin, 1 = scale, 2 = end
                if (status == 0)
                {
                    _isPinching = true;
                    return;
                }
                if (status == 2)
                {
                    _isPinching = false;
                    return;
                }

                // status == 1: apply incremental scale
                float density = (float)DeviceDisplay.MainDisplayInfo.Density;

                if (_model != null && Viewer.IsVisible)
                {
                    ApplyViewerPinch(scaleFactor, screenFocalX, screenFocalY, density);
                }
                else if (ViewerImage.IsVisible)
                {
                    ApplyImagePinch(scaleFactor, screenFocalX, screenFocalY, density);
                }
            }
            catch { }
        }

        void ApplyViewerPinch(float scaleFactor, float screenFocalX, float screenFocalY, float density)
        {
            var nv = Viewer.Handler?.PlatformView as Android.Views.View;
            if (nv == null) return;

            int[] loc = new int[2];
            nv.GetLocationOnScreen(loc);

            // Convert screen px → view-local dips
            float localX = (screenFocalX - loc[0]) / density;
            float localY = (screenFocalY - loc[1]) / density;

            float vw = (float)Viewer.Width;
            float vh = (float)Viewer.Height;
            if (vw <= 0 || vh <= 0) return;

            float centerX = vw / 2f;
            float centerY = vh / 2f;

            float oldScale = _drawable.Scale;
            float newScale = Math.Clamp(oldScale * scaleFactor, 0.05f, 200f);
            float ratio = newScale / oldScale;

            // Keep the focal point stationary
            float dx = localX - centerX - _drawable.PanX;
            float dy = localY - centerY - _drawable.PanY;
            _drawable.PanX += dx * (1f - ratio);
            _drawable.PanY += dy * (1f - ratio);
            _drawable.Scale = newScale;
            Viewer.Invalidate();
        }

        void ApplyImagePinch(float scaleFactor, float screenFocalX, float screenFocalY, float density)
        {
            // Use the Viewer (GraphicsView) as a stable reference — it occupies
            // the exact same Grid cell as ViewerImage but has no Scale/Translation
            // transforms, so GetLocationOnScreen returns a rock-solid position.
            var refView = Viewer.Handler?.PlatformView as Android.Views.View;
            if (refView == null) return;

            float vw = (float)ViewerImage.Width;
            float vh = (float)ViewerImage.Height;
            if (vw <= 0 || vh <= 0) return;

            int[] loc = new int[2];
            refView.GetLocationOnScreen(loc);

            // Focal point in view-local dips (relative to top-left of the cell)
            float localX = (screenFocalX - loc[0]) / density;
            float localY = (screenFocalY - loc[1]) / density;

            // Center of the image view in local dips
            float centerX = vw / 2f;
            float centerY = vh / 2f;

            // Distance from the view center to the focal point, accounting for current pan
            float dx = localX - centerX - _imgPanX;
            float dy = localY - centerY - _imgPanY;

            float oldScale = _imgScale;
            float newScale = Math.Clamp(oldScale * scaleFactor, 0.15f, 10f);
            float ratio = newScale / oldScale;

            // Adjust translation to keep the focal point stationary
            _imgPanX += dx * (1f - ratio);
            _imgPanY += dy * (1f - ratio);
            _imgScale = newScale;

            ViewerImage.Scale = _imgScale;
            ViewerImage.TranslationX = _imgPanX;
            ViewerImage.TranslationY = _imgPanY;
        }
#endif

        async Task TryOpenPendingFileAsync()
        {
            try
            {
                var pending = App.PendingOpenFilePath;
                if (!string.IsNullOrEmpty(pending))
                {
                    App.PendingOpenFilePath = null;
                    if (File.Exists(pending))
                        await OpenFileAsync(pending);
                }
            }
            catch { }
        }

        // Pinch power slider removed from UI; keep `PinchPower` field for zoom behavior.

        private void OnPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (_drawable == null) return;
            if (_isPinching) return;
            if (e.StatusType == GestureStatus.Started)
            {
                // remember starting pan
                _panStartPanX = _drawable.PanX;
                _panStartPanY = _drawable.PanY;
            }
            else if (e.StatusType == GestureStatus.Running)
            {
                // apply sensitivity to finger movement so dragging is less fast
                _drawable.PanX = _panStartPanX + (float)e.TotalX * PanSensitivity;
                _drawable.PanY = _panStartPanY + (float)e.TotalY * PanSensitivity;
                Viewer.Invalidate();
            }
            else if (e.StatusType == GestureStatus.Completed)
            {
                // nothing
            }
        }

        private void OnImagePanUpdated(object sender, PanUpdatedEventArgs e)
        {
            // Allow panning the image when in image mode
            if (ViewerImage == null || ViewerImage.IsVisible == false) return;
            if (_isPinching) return;
            if (e.StatusType == GestureStatus.Started)
            {
                _imgStartTransX = (float)ViewerImage.TranslationX;
                _imgStartTransY = (float)ViewerImage.TranslationY;
            }
            else if (e.StatusType == GestureStatus.Running)
            {
                ViewerImage.TranslationX = _imgStartTransX + (float)e.TotalX;
                ViewerImage.TranslationY = _imgStartTransY + (float)e.TotalY;
                _imgPanX = (float)ViewerImage.TranslationX;
                _imgPanY = (float)ViewerImage.TranslationY;
            }
        }

        private void OnDoubleTapped(object sender, EventArgs e)
        {
            // Cancel any pending single-tap so snap points are not altered during zoom
            _singleTapCts?.Cancel();
            _singleTapCts = null;

            // zoom in on double tap (multiple times)
            // If model exists, zoom GraphicsView as before
            if (_model != null)
            {
                // existing behavior
            }
            else
            {
                // If displaying an image, zoom the Image view centered on tap
                var tappedEventArgsImg = e as TappedEventArgs;
                Point? tapPointImg = tappedEventArgsImg?.GetPosition(ViewerImage);
                if (!tapPointImg.HasValue) return;

                // simple zoom factor
                float zoomFactorImg = 1.5f;

                // toggle zoom in/out
                float targetScaleImg = _imgScale * zoomFactorImg;
                if (targetScaleImg > 8f) // limit max
                    targetScaleImg = 1f; // reset to 1x

                // compute pan so that tap point remains at same relative position
                float vwImg = (float)ViewerImage.Width;
                float vhImg = (float)ViewerImage.Height;
                if (vwImg <= 0 || vhImg <= 0) return;

                float centerXImg = vwImg / 2f;
                float centerYImg = vhImg / 2f;

                float tapXImg = (float)tapPointImg.Value.X;
                float tapYImg = (float)tapPointImg.Value.Y;

                // relative to image center
                float relXImg = (tapXImg - centerXImg - _imgPanX) / _imgScale;
                float relYImg = (tapYImg - centerYImg - _imgPanY) / _imgScale;

                // new pan to keep point stable
                float newPanXImg = _imgPanX + (1 - targetScaleImg / _imgScale) * relXImg * targetScaleImg;
                float newPanYImg = _imgPanY + (1 - targetScaleImg / _imgScale) * relYImg * targetScaleImg;

                _imgScale = targetScaleImg;
                _imgPanX = newPanXImg;
                _imgPanY = newPanYImg;

                // apply transforms
                ViewerImage.Scale = _imgScale;
                ViewerImage.TranslationX = _imgPanX;
                ViewerImage.TranslationY = _imgPanY;

                return;
            }
            // existing GraphicsView behavior continues below
            // get tap position if available
            var tappedEventArgs = e as TappedEventArgs;
            Point? tapPoint = tappedEventArgs?.GetPosition(Viewer);

            var vw = (float)Viewer.Width;
            var vh = (float)Viewer.Height;
            if (vw <= 0 || vh <= 0)
                return;

            float centerX = vw / 2f;
            float centerY = vh / 2f;

            // compute fit scale
            var modelWidth = (_model?.MaxX - _model?.MinX) ?? 1;
            var modelHeight = (_model?.MaxY - _model?.MinY) ?? 1;
            if (modelWidth <= 0) modelWidth = 1;
            if (modelHeight <= 0) modelHeight = 1;
            float fitSx = vw / (float)modelWidth;
            float fitSy = vh / (float)modelHeight;
            float fitS = Math.Min(fitSx, fitSy) * 0.9f;

            // Zoom in using PinchPower
            // Simulate a pinch gesture with scale = 1.05
            double simulatedPinchScale = 1.05;
            double zoomFactor = Math.Pow(simulatedPinchScale, PinchPower);
            if (double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor))
                zoomFactor = 2.5; // fallback

            float zoomInFactor = (float)zoomFactor;

            // Always zoom in (no threshold check)
            float targetScale = _drawable.Scale * zoomInFactor;

            // calculate tap position in model coordinates
            double modelCenterX = (_model?.MinX + _model?.MaxX) / 2.0 ?? 0;
            double modelCenterY = (_model?.MinY + _model?.MaxY) / 2.0 ?? 0;

            // use tap point if available, otherwise use center
            float tapX = tapPoint.HasValue ? (float)tapPoint.Value.X : centerX;
            float tapY = tapPoint.HasValue ? (float)tapPoint.Value.Y : centerY;

            // convert tap screen coords to model coords (before zoom)
            double tapModelX = modelCenterX + (tapX - (centerX + _drawable.PanX)) / (fitS * _drawable.Scale);
            double tapModelY = modelCenterY - (tapY - (centerY + _drawable.PanY)) / (fitS * _drawable.Scale);

            // calculate new pan to keep tap point at same screen position after zoom
            float oldScale = _drawable.Scale;
            float newScale = targetScale;

            double newPanX = _drawable.PanX + fitS * (oldScale - newScale) * (tapModelX - modelCenterX);
            double newPanY = _drawable.PanY + fitS * (newScale - oldScale) * (tapModelY - modelCenterY);

            _drawable.Scale = newScale;
            _drawable.PanX = (float)newPanX;
            _drawable.PanY = (float)newPanY;

            Viewer.Invalidate();
        }

        private async void OnSingleTapped(object sender, EventArgs e)
        {
            // Handle single tap for measurement mode
            if (_drawable.MeasurementMode == MeasurementType.None || _model == null)
                return;

            var tappedEventArgs = e as TappedEventArgs;
            Point? tapPoint = tappedEventArgs?.GetPosition(Viewer);

            if (!tapPoint.HasValue)
                return;

            // Debounce: wait a short time to let a potential double-tap cancel this action
            _singleTapCts?.Cancel();
            var cts = new CancellationTokenSource();
            _singleTapCts = cts;
            try
            {
                await Task.Delay(200, cts.Token);
            }
            catch (TaskCanceledException)
            {
                return; // double-tap detected, abort measurement
            }

            // Convert screen coordinates to model coordinates
            var vw = (float)Viewer.Width;
            var vh = (float)Viewer.Height;
            if (vw <= 0 || vh <= 0)
                return;

            float centerX = vw / 2f;
            float centerY = vh / 2f;

            // Compute fit scale (same as in Draw)
            var modelWidth = (_model?.MaxX - _model?.MinX) ?? 1;
            var modelHeight = (_model?.MaxY - _model?.MinY) ?? 1;
            if (modelWidth <= 0) modelWidth = 1;
            if (modelHeight <= 0) modelHeight = 1;
            float fitSx = vw / (float)modelWidth;
            float fitSy = vh / (float)modelHeight;
            float fitS = Math.Min(fitSx, fitSy) * 0.9f;

            // Convert tap screen coords to model coords
            double modelCenterX = (_model?.MinX + _model?.MaxX) / 2.0 ?? 0;
            double modelCenterY = (_model?.MinY + _model?.MaxY) / 2.0 ?? 0;

            float tapX = (float)tapPoint.Value.X;
            float tapY = (float)tapPoint.Value.Y;

            double tapModelX = modelCenterX + (tapX - (centerX + _drawable.PanX)) / (fitS * _drawable.Scale);
            double tapModelY = modelCenterY - (tapY - (centerY + _drawable.PanY)) / (fitS * _drawable.Scale);

            // Find nearest snap point (extrémités, milieux, centres, etc.)
            PointF? snapPoint = FindNearestSnapPoint((float)tapModelX, (float)tapModelY, fitS * _drawable.Scale);

            PointF finalPoint = snapPoint ?? new PointF((float)tapModelX, (float)tapModelY);

            // If in manual reselection mode, replace the designated point
            if (_reselectingPoint == 1 || _reselectingPoint == 2)
            {
                if (!snapPoint.HasValue)
                    return;

                if (_reselectingPoint == 1)
                    _drawable.MeasurePoint1 = snapPoint.Value;
                else
                    _drawable.MeasurePoint2 = snapPoint.Value;

                _reselectingPoint = 0;
                UpdateReselectButtonColors();
            }
            // Normal flow: store the snap point
            else if (!_drawable.MeasurePoint1.HasValue)
            {
                // Require a snap; do not allow placing points in empty space
                if (!snapPoint.HasValue)
                    return;

                _drawable.MeasurePoint1 = snapPoint.Value;
                // prepare cycle candidates for this first point
                _cycleCandidates.Clear();
                _cycleCandidateIndex = -1;
                // gather candidates near this point
                _cycleCandidates = GetSnapCandidates((float)snapPoint.Value.X, (float)snapPoint.Value.Y, fitS * _drawable.Scale);
                if (_cycleCandidates.Count > 0)
                {
                    _cycleCandidateIndex = 0;
                }
            }

            else if (!_drawable.MeasurePoint2.HasValue)
            {
                if (!snapPoint.HasValue)
                    return;

                _drawable.MeasurePoint2 = snapPoint.Value;
            }
            else
            {
                // Reset and start over only if tapped on a snap
                if (!snapPoint.HasValue)
                    return;

                _drawable.MeasurePoint1 = snapPoint.Value;
                _drawable.MeasurePoint2 = null;
                _cycleCandidates.Clear();
                _cycleCandidateIndex = -1;
                _cycleCandidates = GetSnapCandidates((float)snapPoint.Value.X, (float)snapPoint.Value.Y, fitS * _drawable.Scale);
                if (_cycleCandidates.Count > 0) _cycleCandidateIndex = 0;
            }

            // Update UI so the cycle buttons enable state reflects selected points
            UpdateMeasurementUI();
            {
                bool show = _drawable.MeasurementMode != MeasurementType.None && _model != null;
                CycleSnapPoint1Button.IsVisible = show;
                CycleSnapPoint1Button.IsEnabled = show && _drawable.MeasurePoint1.HasValue;
                CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
                CycleSnapPoint2Button.IsVisible = show;
                CycleSnapPoint2Button.IsEnabled = show && _drawable.MeasurePoint2.HasValue;
                CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;
            }

            Viewer.Invalidate();
        }

        // Find the nearest snap point (endpoint, midpoint, center, etc.) to the given model coordinates
        private PointF? FindNearestSnapPoint(float modelX, float modelY, float screenScale)
        {
            if (_model == null) return null;

            float snapTolerance = 50f / screenScale;
            float snapToleranceSq = snapTolerance * snapTolerance;

            PointF? nearestPoint = null;
            float nearestDistSq = float.MaxValue;

            var layerDict = _model.LayerDict;
            foreach (var entity in _model.Entities)
            {
                bool visible = true;
                if (layerDict.TryGetValue(entity.LayerId, out var lay))
                    visible = lay.Visible;
                if (!visible) continue;

                switch (entity)
                {
                    case TagLine line:
                        CheckSnap(line.X1, line.Y1, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap(line.X2, line.Y2, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap((line.X1 + line.X2) / 2f, (line.Y1 + line.Y2) / 2f, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        {
                            float lx = line.X2 - line.X1;
                            float ly = line.Y2 - line.Y1;
                            float denom = lx * lx + ly * ly;
                            if (denom > 1e-6f)
                            {
                                float t = ((modelX - line.X1) * lx + (modelY - line.Y1) * ly) / denom;
                                if (t < 0) t = 0;
                                if (t > 1) t = 1;
                                CheckSnap(line.X1 + t * lx, line.Y1 + t * ly, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                            }
                        }
                        break;

                    case TagCircle circle:
                        {
                            float dxC = circle.CX - modelX;
                            float dyC = circle.CY - modelY;
                            float distCSq = dxC * dxC + dyC * dyC;
                            if (distCSq <= snapToleranceSq)
                                return new PointF(circle.CX, circle.CY);
                        }
                        CheckSnap(circle.CX, circle.CY, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap(circle.CX + circle.R, circle.CY, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap(circle.CX, circle.CY + circle.R, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap(circle.CX - circle.R, circle.CY, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        CheckSnap(circle.CX, circle.CY - circle.R, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        {
                            double angleToClickCirc = Math.Atan2(modelY - circle.CY, modelX - circle.CX);
                            CheckSnap(circle.CX + circle.R * (float)Math.Cos(angleToClickCirc),
                                      circle.CY + circle.R * (float)Math.Sin(angleToClickCirc),
                                      modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        }
                        break;

                    case TagArc arc:
                        {
                            float dxC = arc.CX - modelX;
                            float dyC = arc.CY - modelY;
                            float distCSq = dxC * dxC + dyC * dyC;
                            if (distCSq <= snapToleranceSq)
                                return new PointF(arc.CX, arc.CY);
                        }
                        CheckSnap(arc.CX, arc.CY, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        {
                            double startRad = arc.StartAngle * Math.PI / 180.0;
                            double sweepRad = arc.SweepAngle * Math.PI / 180.0;
                            sweepRad = ((sweepRad % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                            if (sweepRad <= 0) sweepRad += 2 * Math.PI;
                            double endRad = startRad + sweepRad;
                            CheckSnap(arc.CX + arc.R * (float)Math.Cos(startRad), arc.CY + arc.R * (float)Math.Sin(startRad),
                                      modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                            CheckSnap(arc.CX + arc.R * (float)Math.Cos(endRad), arc.CY + arc.R * (float)Math.Sin(endRad),
                                      modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                            double midRad = startRad + sweepRad / 2.0;
                            CheckSnap(arc.CX + arc.R * (float)Math.Cos(midRad), arc.CY + arc.R * (float)Math.Sin(midRad),
                                      modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                            double angleToClick = Math.Atan2(modelY - arc.CY, modelX - arc.CX);
                            double angleDiff = ((angleToClick - startRad) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                            if (angleDiff <= sweepRad + 1e-6)
                            {
                                CheckSnap(arc.CX + arc.R * (float)Math.Cos(angleToClick), arc.CY + arc.R * (float)Math.Sin(angleToClick),
                                          modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                            }
                        }
                        break;

                    case TagPoint point:
                        CheckSnap(point.X, point.Y, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        break;

                    case TagText text:
                        CheckSnap(text.X, text.Y, modelX, modelY, snapToleranceSq, ref nearestDistSq, ref nearestPoint);
                        break;
                }
            }

            return nearestPoint;
        }

        static void CheckSnap(float px, float py, float modelX, float modelY, float snapToleranceSq, ref float nearestDistSq, ref PointF? nearest)
        {
            float dx = px - modelX;
            float dy = py - modelY;
            float distSq = dx * dx + dy * dy;
            if (distSq < snapToleranceSq && distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = new PointF(px, py);
            }
        }

        // Return a list of candidate snap points near the given model coords (ordered)
        private List<PointF> GetSnapCandidates(float modelX, float modelY, float screenScale)
        {
            var list = new List<PointF>();
            if (_model == null) return list;

            float snapTolerance = 50f / screenScale;
            float snapToleranceSq = snapTolerance * snapTolerance;

            // Collect snap points and circle/arc centers in a single pass
            var centers = new HashSet<(int, int)>(); // track center grid cells for fast lookup
            var centerList = new List<PointF>();
            var layerDict = _model.LayerDict;
            foreach (var entity in _model.Entities)
            {
                bool visible = true;
                if (layerDict.TryGetValue(entity.LayerId, out var lay))
                    visible = lay.Visible;
                if (!visible) continue;

                switch (entity)
                {
                    case TagLine line:
                        list.Add(new PointF(line.X1, line.Y1));
                        list.Add(new PointF(line.X2, line.Y2));
                        list.Add(new PointF((line.X1 + line.X2) / 2f, (line.Y1 + line.Y2) / 2f));
                        {
                            float lx = line.X2 - line.X1;
                            float ly = line.Y2 - line.Y1;
                            float denom = lx * lx + ly * ly;
                            if (denom > 1e-6f)
                            {
                                float t = ((modelX - line.X1) * lx + (modelY - line.Y1) * ly) / denom;
                                if (t < 0) t = 0; if (t > 1) t = 1;
                                list.Add(new PointF(line.X1 + t * lx, line.Y1 + t * ly));
                            }
                        }
                        break;
                    case TagCircle c:
                        list.Add(new PointF(c.CX, c.CY));
                        centerList.Add(new PointF(c.CX, c.CY));
                        list.Add(new PointF(c.CX + c.R, c.CY));
                        list.Add(new PointF(c.CX, c.CY + c.R));
                        list.Add(new PointF(c.CX - c.R, c.CY));
                        list.Add(new PointF(c.CX, c.CY - c.R));
                        double angleToClick = Math.Atan2(modelY - c.CY, modelX - c.CX);
                        list.Add(new PointF(c.CX + c.R * (float)Math.Cos(angleToClick), c.CY + c.R * (float)Math.Sin(angleToClick)));
                        break;
                    case TagArc a:
                        double startRad = a.StartAngle * Math.PI / 180.0;
                        double sweepRad = a.SweepAngle * Math.PI / 180.0;
                        sweepRad = ((sweepRad % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                        if (sweepRad <= 0) sweepRad += 2 * Math.PI;
                        double endRad = startRad + sweepRad;
                        list.Add(new PointF(a.CX, a.CY));
                        centerList.Add(new PointF(a.CX, a.CY));
                        list.Add(new PointF(a.CX + a.R * (float)Math.Cos(startRad), a.CY + a.R * (float)Math.Sin(startRad)));
                        list.Add(new PointF(a.CX + a.R * (float)Math.Cos(endRad), a.CY + a.R * (float)Math.Sin(endRad)));
                        double midRad = startRad + sweepRad / 2.0;
                        list.Add(new PointF(a.CX + a.R * (float)Math.Cos(midRad), a.CY + a.R * (float)Math.Sin(midRad)));
                        double angClick = Math.Atan2(modelY - a.CY, modelX - a.CX);
                        double angleDiff = ((angClick - startRad) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                        if (angleDiff <= sweepRad + 1e-6)
                        {
                            list.Add(new PointF(a.CX + a.R * (float)Math.Cos(angClick), a.CY + a.R * (float)Math.Sin(angClick)));
                        }
                        break;
                    case TagPoint p:
                        list.Add(new PointF(p.X, p.Y));
                        break;
                    case TagText t:
                        list.Add(new PointF(t.X, t.Y));
                        break;
                }
            }

            // Filter using squared distance (avoid Math.Sqrt) and order by distance
            var filtered = list
                .Where(sp => (sp.X - modelX) * (sp.X - modelX) + (sp.Y - modelY) * (sp.Y - modelY) <= snapToleranceSq)
                .OrderBy(sp => (sp.X - modelX) * (sp.X - modelX) + (sp.Y - modelY) * (sp.Y - modelY))
                .ToList();

            // Prefer circle/arc centers: place matching centers first
            if (centerList.Count > 0)
            {
                const float EPS = 1e-3f;
                var centerMatches = new List<PointF>();
                var otherMatches = new List<PointF>();

                foreach (var sp in filtered)
                {
                    bool isCenter = false;
                    foreach (var c in centerList)
                    {
                        if (Math.Abs(sp.X - c.X) < EPS && Math.Abs(sp.Y - c.Y) < EPS)
                        {
                            isCenter = true;
                            break;
                        }
                    }
                    if (isCenter) centerMatches.Add(sp); else otherMatches.Add(sp);
                }

                centerMatches = centerMatches.OrderBy(sp => (sp.X - modelX) * (sp.X - modelX) + (sp.Y - modelY) * (sp.Y - modelY)).ToList();
                filtered = centerMatches.Concat(otherMatches).ToList();
            }

            return filtered;
        }



        // (Moved) radius detection now implemented on the drawable so drawing code can access it.

        // sample counter removed

        private async void OnOpenTagClicked(object sender, EventArgs e)
        {
            try
            {
                string? filePath = null;

                // Always try the custom FileBrowserPage first (request storage permissions on Android)
                try
                {
                    bool hasAccess = await RequestStoragePermissionAsync();
                    if (hasAccess)
                    {
                        var tcs = new TaskCompletionSource<string?>();
                        var browserPage = new FileBrowserPage(tcs);
                        await Navigation.PushModalAsync(browserPage);
                        filePath = await tcs.Task;
                    }
                }
                catch { }

                // Fallback: system FilePicker if custom browser failed or returned null
                if (string.IsNullOrEmpty(filePath))
                {
                    try
                    {
                        var result = await FilePicker.Default.PickAsync(new PickOptions
                        {
                            PickerTitle = "Choisir un fichier (.tag, .dxf, .dwg, .pdf, .xls, .xlsx, .jpg, .png, .bmp, .gif, .webp, .tif)"
                        });
                        if (result != null)
                        {
                            if (!string.IsNullOrEmpty(result.FullPath) && File.Exists(result.FullPath))
                                filePath = result.FullPath;
                            else
                            {
                                var dest = Path.Combine(FileSystem.CacheDirectory, result.FileName ?? Guid.NewGuid().ToString());
                                using (var src = await result.OpenReadAsync())
                                using (var dst = File.Create(dest))
                                {
                                    await src.CopyToAsync(dst);
                                }
                                filePath = dest;
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(filePath))
                    return;

                // Hide the soft keyboard when a file was chosen so it does not remain visible
                // on the main page (useful when returning from the in-app browser/search).
                try { HideKeyboard(); } catch { }

                var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                var fileName = System.IO.Path.GetFileName(filePath);

                if (ext == ".tag")
                {
                    // parse tag file on background thread to keep UI responsive
                    TagModel parsedModel;
                    using (var stream = File.OpenRead(filePath))
                    {
                        parsedModel = await Task.Run(() => TagParser.Parse(stream));
                    }
                    _model = parsedModel;
                    _drawable.Model = _model;
                    // reset view transform so the new file is shown fitted
                    _drawable.ResetTransform();
                    // reset measurement state from any previous file
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    // show axes marker when a .tag is opened
                    _drawable.ShowAxes = true;
                    // enable full measurement buttons when a .tag is opened
                    SetShellButtonsMode(ShellButtonsMode.Full);
                    // ensure measurement UI (including cycle button) reflects new model state
                    UpdateMeasurementUI();
                    // ensure GraphicsView visible
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp" || ext == ".tif" || ext == ".tiff")
                {
                    // load image into ViewerImage
                    try
                    {
                        Viewer.IsVisible = false;
                        ViewerImage.IsVisible = true;
                        ViewerWebView.IsVisible = false;
                        // reset model
                        _model = null;
                        _drawable.Model = null;
                        _drawable.ShowAxes = false;
                        _drawable.MeasurementMode = MeasurementType.None;
                        _drawable.MeasurePoint1 = null;
                        _drawable.MeasurePoint2 = null;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);

                        // Keep ZoomOut and Fit enabled for images
                        if (Application.Current?.MainPage is AppShell shellImg)
                        {
                            var btnZoomOut = shellImg.FindByName<Button>("ZoomOutButton");
                            if (btnZoomOut != null)
                            {
                                btnZoomOut.IsEnabled = true;
                                btnZoomOut.Opacity = 1.0;
                            }
                            var btnFit = shellImg.FindByName<Button>("FitButton");
                            if (btnFit != null)
                            {
                                btnFit.IsEnabled = true;
                                btnFit.Opacity = 1.0;
                            }
                        }

                        // Reset image transforms so the new image starts fitted
                        _imgScale = 1f;
                        _imgPanX = 0f;
                        _imgPanY = 0f;
                        ViewerImage.Scale = 1f;
                        ViewerImage.TranslationX = 0;
                        ViewerImage.TranslationY = 0;

                        var bytes = await File.ReadAllBytesAsync(filePath);
                        ViewerImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                    }
                    catch { }
                }
                else if (ext == ".dxf")
                {
                    TagModel parsedModel;
                    using (var stream = File.OpenRead(filePath))
                    {
                        parsedModel = await Task.Run(() => DxfParser.Parse(stream));
                    }
                    _model = parsedModel;
                    _drawable.Model = _model;
                    _drawable.ResetTransform();
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    _drawable.ShowAxes = true;
                    // enable full measurement buttons for DXF
                    SetShellButtonsMode(ShellButtonsMode.Full);
                    UpdateMeasurementUI();
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                }
                else if (ext == ".pdf")
                {
                    _model = null;
                    _drawable.Model = null;
                    _drawable.ShowAxes = false;
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                    UpdateMeasurementUI();
                    byte[]? pdfBytes = await RenderPdfFirstPageAsync(filePath);
                    if (pdfBytes != null)
                    {
                        Viewer.IsVisible = false;
                        ViewerImage.IsVisible = true;
                        ViewerWebView.IsVisible = false;
                        _imgScale = 1f;
                        _imgPanX = 0f;
                        _imgPanY = 0f;
                        ViewerImage.Scale = 1f;
                        ViewerImage.TranslationX = 0;
                        ViewerImage.TranslationY = 0;
                        ViewerImage.Source = ImageSource.FromStream(() => new MemoryStream(pdfBytes));
                        if (Application.Current?.MainPage is AppShell shellPdf)
                        {
                            var btnZoomOut = shellPdf.FindByName<Button>("ZoomOutButton");
                            if (btnZoomOut != null) { btnZoomOut.IsEnabled = true; btnZoomOut.Opacity = 1.0; }
                            var btnFit = shellPdf.FindByName<Button>("FitButton");
                            if (btnFit != null) { btnFit.IsEnabled = true; btnFit.Opacity = 1.0; }
                        }
                    }
                    else
                    {
                        Viewer.IsVisible = true;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = false;
                        await DisplayAlert("PDF", "Impossible de rendre ce fichier PDF.", "OK");
                    }
                }
                else
                {
                    // unknown extension - notify the user
                    _model = null;
                    _drawable.Model = null;
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                    UpdateMeasurementUI();
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                    await DisplayAlert("Format non supporté", $"Le format '{ext}' n'est pas pris en charge. Formats supportés : .tag, .dxf, .dwg, .pdf, .xls, .xlsx, .png, .jpg, .bmp, .gif, .webp, .tif", "OK");
                }

                // Set PinchPower to 20 when opening a file
                PinchPower = 20.0;
                // Zoom slider removed from UI; no UI to update here.

                Viewer.Invalidate();
                // update shell title status as well
                if (Shell.Current is AppShell)
                {
                    try
                    {
                        if (Application.Current?.MainPage is AppShell shell)
                        {
                            var lbl = shell.FindByName<Label>("TopStatusLabel");
                            if (lbl != null) lbl.Text = fileName;
                        }
                        FileNameLabel.Text = fileName;
                    }
                    catch { }
                    // Save to recent files list
                    AddRecentFile(filePath);
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Erreur", ex.Message, "OK");
            }
        }

        // Add a file path to the recent list stored in Preferences (most-recent first)
        void AddRecentFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var key = "RecentFiles";
                var raw = Preferences.Get(key, "");
                var list = new List<string>();
                if (!string.IsNullOrEmpty(raw))
                    list = raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
                // remove existing occurrence
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                // limit to 20 entries
                if (list.Count > 20) list = list.Take(20).ToList();
                Preferences.Set(key, string.Join('|', list));
            }
            catch { }
        }

        // Open a file by path (used by shell recent previews)
        public async Task OpenFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                // Ensure keyboard is hidden when opening a file programmatically
                try { HideKeyboard(); } catch { }
                var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                var fileName = System.IO.Path.GetFileName(filePath);

                if (ext == ".tag")
                {
                    TagModel parsedModel;
                    using (var stream = File.OpenRead(filePath))
                    {
                        parsedModel = await Task.Run(() => TagParser.Parse(stream));
                    }
                    _model = parsedModel;
                    _drawable.Model = _model;
                    _drawable.ResetTransform();
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    _drawable.ShowAxes = true;
                    // enable full measurement buttons for DXF
                    SetShellButtonsMode(ShellButtonsMode.Full);
                    UpdateMeasurementUI();
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp" || ext == ".tif" || ext == ".tiff")
                {
                    try
                    {
                        Viewer.IsVisible = false;
                        ViewerImage.IsVisible = true;
                        ViewerWebView.IsVisible = false;
                        _model = null;
                        _drawable.Model = null;
                        _drawable.ShowAxes = false;
                        _drawable.MeasurementMode = MeasurementType.None;
                        _drawable.MeasurePoint1 = null;
                        _drawable.MeasurePoint2 = null;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);

                        _imgScale = 1f;
                        _imgPanX = 0f;
                        _imgPanY = 0f;
                        ViewerImage.Scale = 1f;
                        ViewerImage.TranslationX = 0;
                        ViewerImage.TranslationY = 0;

                        var bytes = await File.ReadAllBytesAsync(filePath);
                        ViewerImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                    }
                    catch { }
                }
                else if (ext == ".dxf")
                {
                    TagModel parsedModel;
                    using (var stream = File.OpenRead(filePath))
                    {
                        parsedModel = await Task.Run(() => DxfParser.Parse(stream));
                    }
                    _model = parsedModel;
                    _drawable.Model = _model;
                    _drawable.ResetTransform();
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    _drawable.ShowAxes = true;
                    SetShellButtonsMode(ShellButtonsMode.Full);
                    UpdateMeasurementUI();
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                }
                else if (ext == ".dwg")
                {
                    TagModel? parsedDwg = null;
                    try
                    {
                        using var stream = File.OpenRead(filePath);
                        parsedDwg = await Task.Run(() => DwgParser.Parse(stream));
                    }
                    catch { }

                    if (parsedDwg != null && parsedDwg.Entities.Count > 0)
                    {
                        _model = parsedDwg;
                        _drawable.Model = _model;
                        _drawable.ResetTransform();
                        _drawable.MeasurementMode = MeasurementType.None;
                        _drawable.MeasurePoint1 = null;
                        _drawable.MeasurePoint2 = null;
                        _drawable.ShowAxes = true;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                        UpdateMeasurementUI();
                        Viewer.IsVisible = true;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = false;
                    }
                    else
                    {
                        _model = null;
                        _drawable.Model = null;
                        _drawable.ShowAxes = false;
                        _drawable.MeasurementMode = MeasurementType.None;
                        _drawable.MeasurePoint1 = null;
                        _drawable.MeasurePoint2 = null;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                        UpdateMeasurementUI();
                        Viewer.IsVisible = true;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = false;
                        string dwgError = DwgParser.LastError
                            ?? (parsedDwg != null && parsedDwg.Entities.Count == 0
                                ? "Aucune entité lisible trouvée dans ce fichier DWG."
                                : "Aucune entité trouvée.");
                        bool openDwg = await DisplayAlert("Format DWG",
                            $"Impossible de lire ce fichier DWG.\n{dwgError}\nOuvrir avec une autre application ?",
                            "Ouvrir", "Annuler");
                        if (openDwg)
                            await OpenWithExternalAppAsync(filePath, "application/octet-stream");
                        return;
                    }
                }
                else if (ext == ".pdf")
                {
                    _model = null;
                    _drawable.Model = null;
                    _drawable.ShowAxes = false;
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                        SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                    UpdateMeasurementUI();
                    byte[]? pdfBytes = await RenderPdfFirstPageAsync(filePath);
                    if (pdfBytes != null)
                    {
                        Viewer.IsVisible = false;
                        ViewerImage.IsVisible = true;
                        ViewerWebView.IsVisible = false;
                        _imgScale = 1f;
                        _imgPanX = 0f;
                        _imgPanY = 0f;
                        ViewerImage.Scale = 1f;
                        ViewerImage.TranslationX = 0;
                        ViewerImage.TranslationY = 0;
                        ViewerImage.Source = ImageSource.FromStream(() => new MemoryStream(pdfBytes));
                        if (Application.Current?.MainPage is AppShell shellPdf)
                        {
                            var btnZoomOut = shellPdf.FindByName<Button>("ZoomOutButton");
                            if (btnZoomOut != null) { btnZoomOut.IsEnabled = true; btnZoomOut.Opacity = 1.0; }
                            var btnFit = shellPdf.FindByName<Button>("FitButton");
                            if (btnFit != null) { btnFit.IsEnabled = true; btnFit.Opacity = 1.0; }
                        }
                    }
                    else
                    {
                        Viewer.IsVisible = true;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = false;
                    }
                }
                else if (ext == ".xls" || ext == ".xlsx")
                {
                    _model = null;
                    _drawable.Model = null;
                    _drawable.ShowAxes = false;
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                    UpdateMeasurementUI();

                    try
                    {
                        string html = await Task.Run(() => ExcelParser.ToHtml(filePath));
                        Viewer.IsVisible = false;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = true;
                        ViewerWebView.Source = new HtmlWebViewSource { Html = html };
                    }
                    catch
                    {
                        Viewer.IsVisible = true;
                        ViewerImage.IsVisible = false;
                        ViewerWebView.IsVisible = false;
                        string mimeType = ext == ".xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/vnd.ms-excel";
                        bool openExt = await DisplayAlert("Excel",
                            "Impossible de lire ce fichier Excel.\nOuvrir avec une autre application ?",
                            "Ouvrir", "Annuler");
                        if (openExt)
                            await OpenWithExternalAppAsync(filePath, mimeType);
                    }
                }
                else
                {
                    _model = null;
                    _drawable.Model = null;
                    _drawable.MeasurementMode = MeasurementType.None;
                    _drawable.MeasurePoint1 = null;
                    _drawable.MeasurePoint2 = null;
                    SetShellButtonsMode(ShellButtonsMode.FitZoomOnly);
                    UpdateMeasurementUI();
                    Viewer.IsVisible = true;
                    ViewerImage.IsVisible = false;
                    ViewerWebView.IsVisible = false;
                    await DisplayAlert("Format non supporté", $"Le format '{ext}' n'est pas pris en charge.", "OK");
                    return;
                }

                PinchPower = 20.0;
                Viewer.Invalidate();
                try
                {
                    if (Application.Current?.MainPage is AppShell shell)
                    {
                        var lbl = shell.FindByName<Label>("TopStatusLabel");
                        if (lbl != null) lbl.Text = fileName;
                    }
                    FileNameLabel.Text = fileName;
                }
                catch { }

                AddRecentFile(filePath);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Erreur", ex.Message, "OK");
            }
        }

        /// <summary>
        /// Requests file system access on Android (MANAGE_EXTERNAL_STORAGE for API 30+,
        /// READ_EXTERNAL_STORAGE for older versions). Returns true if access was granted.
        /// </summary>
        static async Task<bool> RequestStoragePermissionAsync()
        {
#if ANDROID
            // Android 11+ (API 30): need MANAGE_EXTERNAL_STORAGE
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
            {
                if (!Android.OS.Environment.IsExternalStorageManager)
                {
                    try
                    {
                        // Open the app-specific "Accès à tous les fichiers" settings page
                        // so the user can grant the permission to this app.
                        var pkg = Platform.CurrentActivity?.PackageName ?? string.Empty;
                        var uri = Android.Net.Uri.Parse($"package:{pkg}");
                        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, uri);
                        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                        Platform.CurrentActivity?.StartActivity(intent);

                        // Poll for the permission for a short period (user must toggle the setting).
                        // There is no supported way to grant MANAGE_EXTERNAL_STORAGE silently.
                        const int attempts = 40; // ~20s total (40 * 500ms)
                        for (int i = 0; i < attempts; i++)
                        {
                            await Task.Delay(500);
                            if (Android.OS.Environment.IsExternalStorageManager)
                                return true;
                        }

                        return Android.OS.Environment.IsExternalStorageManager;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return true;
            }
            else
            {
                // Android 10 and below: use standard READ_EXTERNAL_STORAGE permission
                var status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
                if (status != PermissionStatus.Granted)
                    status = await Permissions.RequestAsync<Permissions.StorageRead>();
                return status == PermissionStatus.Granted;
            }
#else
            await Task.CompletedTask;
            return true;
#endif
        }

        private void OnResetClicked(object sender, EventArgs e)
        {
            _model = null;
            _drawable.Model = null;
            _drawable.ShowAxes = false;
            Viewer.Invalidate();
            // No file opened => only Open button active
            SetShellButtonsMode(ShellButtonsMode.None);
            // update shell title status
            try
            {
                if (Application.Current?.MainPage is AppShell shell)
                {
                    var lbl = shell.FindByName<Label>("TopStatusLabel");
                    if (lbl != null) lbl.Text = "Aucun fichier chargé";
                }
            }
            catch { }

            FileNameLabel.Text = "Aucun fichier chargé";
        }

        // Layers UI removed; helpers not needed

        // Export and library integration removed per request

        private void OnFitClicked(object sender, EventArgs e)
        {
            // If a vector model is loaded, reset its transform
            if (_model != null)
            {
                if (_drawable == null) return;
                _drawable.ResetTransform();
                Viewer.IsVisible = true;
                ViewerImage.IsVisible = false;
                Viewer.Invalidate();
                return;
            }

            // If an image is displayed, reset image transforms to fit
            try
            {
                if (ViewerImage != null && ViewerImage.IsVisible)
                {
                    _imgScale = 1f;
                    _imgPanX = 0f;
                    _imgPanY = 0f;
                    ViewerImage.Scale = 1f;
                    ViewerImage.TranslationX = 0;
                    ViewerImage.TranslationY = 0;
                    // ensure image is visible
                    ViewerImage.IsVisible = true;
                    Viewer.IsVisible = false;
                }
            }
            catch { }
        }

        private void OnMeasureDoubleClicked(object sender, EventArgs e)
        {
            if (_model == null) return;

            // Cancel any pending reselection mode
            _reselectingPoint = 0;
            UpdateReselectButtonColors();

            // Toggle measurement double mode
            if (_drawable.MeasurementMode == MeasurementType.Double)
                _drawable.MeasurementMode = MeasurementType.None;
            else
                _drawable.MeasurementMode = MeasurementType.Double;

            // Reset measurement points when exiting measurement mode
            if (_drawable.MeasurementMode == MeasurementType.None)
            {
                _drawable.MeasurePoint1 = null;
                _drawable.MeasurePoint2 = null;
            }

            // Update status label and button colors
            UpdateMeasurementUI();
            {
                bool show = _drawable.MeasurementMode != MeasurementType.None && _model != null;
                CycleSnapPoint1Button.IsVisible = show;
                CycleSnapPoint1Button.IsEnabled = show && _drawable.MeasurePoint1.HasValue;
                CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
                CycleSnapPoint2Button.IsVisible = show;
                CycleSnapPoint2Button.IsEnabled = show && _drawable.MeasurePoint2.HasValue;
                CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;
            }

            Viewer.Invalidate();
        }

        private void OnMeasureLinearClicked(object sender, EventArgs e)
        {
            if (_model == null) return;

            // Cancel any pending reselection mode
            _reselectingPoint = 0;
            UpdateReselectButtonColors();

            // Toggle measurement linear mode
            if (_drawable.MeasurementMode == MeasurementType.Linear)
                _drawable.MeasurementMode = MeasurementType.None;
            else
                _drawable.MeasurementMode = MeasurementType.Linear;

            // Reset measurement points when exiting measurement mode
            if (_drawable.MeasurementMode == MeasurementType.None)
            {
                _drawable.MeasurePoint1 = null;
                _drawable.MeasurePoint2 = null;
            }

            // Update status label and button colors
            UpdateMeasurementUI();
            {
                bool show = _drawable.MeasurementMode != MeasurementType.None && _model != null;
                CycleSnapPoint1Button.IsVisible = show;
                CycleSnapPoint1Button.IsEnabled = show && _drawable.MeasurePoint1.HasValue;
                CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
                CycleSnapPoint2Button.IsVisible = show;
                CycleSnapPoint2Button.IsEnabled = show && _drawable.MeasurePoint2.HasValue;
                CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;
            }

            Viewer.Invalidate();
        }

        private void OnToggleDimensionsClicked(object sender, EventArgs e)
        {
            _drawable.ShowGeometry = !_drawable.ShowGeometry;
            try
            {
                if (Application.Current?.MainPage is AppShell shell)
                {
                    var btn = shell.FindByName<Button>("ToggleDimensionsButton");
                    if (btn != null)
                    {
                        btn.BackgroundColor = _drawable.ShowGeometry ? null : Colors.Green;
                        btn.TextColor = Colors.White;
                    }
                }
            }
            catch { }
            Viewer.Invalidate();
        }

        private void UpdateMeasurementUI()
        {
            try
            {
                if (Application.Current?.MainPage is AppShell shell)
                {
                    var lbl = shell.FindByName<Label>("TopStatusLabel");
                    if (lbl != null)
                    {
                        lbl.Text = _drawable.MeasurementMode switch
                        {
                            MeasurementType.Double => "Mode Mesure Ortho: Touchez 2 points",
                            MeasurementType.Linear => "Mode Mesure Linéaire: Touchez 2 points",
                            _ => System.IO.Path.GetFileName(_model?.ToString() ?? "Fichier chargé")
                        };
                    }

                    // Change button colors based on measurement mode
                    var btnDouble = shell.FindByName<Button>("MeasureDoubleButton");
                    if (btnDouble != null)
                    {
                        if (_drawable.MeasurementMode == MeasurementType.Double)
                            btnDouble.BackgroundColor = Colors.Green;
                        else
                            btnDouble.ClearValue(Button.BackgroundColorProperty);
                        btnDouble.TextColor = Colors.White;
                    }

                    var btnLinear = shell.FindByName<Button>("MeasureLinearButton");
                    if (btnLinear != null)
                    {
                        if (_drawable.MeasurementMode == MeasurementType.Linear)
                            btnLinear.BackgroundColor = Colors.Green;
                        else
                            btnLinear.ClearValue(Button.BackgroundColorProperty);
                        btnLinear.TextColor = Colors.White;
                    }

                    // Show or hide the cycle snap button when measurement mode is active
                    // try shell-located buttons first
                    var cycleP1 = shell.FindByName<Button>("CycleSnapPoint1Button");
                    var cycleP2 = shell.FindByName<Button>("CycleSnapPoint2Button");
                    bool show = _drawable.MeasurementMode != MeasurementType.None && _model != null;
                    if (cycleP1 != null) { cycleP1.IsVisible = show; cycleP1.IsEnabled = show && _drawable.MeasurePoint1.HasValue; cycleP1.Opacity = cycleP1.IsEnabled ? 1.0 : 0.5; }
                    if (cycleP2 != null) { cycleP2.IsVisible = show; cycleP2.IsEnabled = show && _drawable.MeasurePoint2.HasValue; cycleP2.Opacity = cycleP2.IsEnabled ? 1.0 : 0.5; }
                    // page-local buttons
                    CycleSnapPoint1Button.IsVisible = show;
                    CycleSnapPoint1Button.IsEnabled = show && _drawable.MeasurePoint1.HasValue;
                    CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
                    CycleSnapPoint2Button.IsVisible = show;
                    CycleSnapPoint2Button.IsEnabled = show && _drawable.MeasurePoint2.HasValue;
                    CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;
                }
            }
            catch { }
        }

        // Shell button modes: None = only Open active; FitZoomOnly = Ajuster+Dézoom active; Full = all measurement buttons active
        enum ShellButtonsMode { None, FitZoomOnly, Full }

        // Set shell buttons state depending on opened file type
        private void SetShellButtonsMode(ShellButtonsMode mode)
        {
            try
            {
                if (Application.Current?.MainPage is AppShell shell)
                {
                    var btnFit = shell.FindByName<Button>("FitButton");
                    if (btnFit != null)
                    {
                        bool fitEnabled = mode != ShellButtonsMode.None;
                        btnFit.IsEnabled = fitEnabled;
                        btnFit.Opacity = fitEnabled ? 1.0 : 0.5;
                    }

                    var btnDouble = shell.FindByName<Button>("MeasureDoubleButton");
                    if (btnDouble != null)
                    {
                        bool dblEnabled = mode == ShellButtonsMode.Full;
                        btnDouble.IsEnabled = dblEnabled;
                        btnDouble.Opacity = dblEnabled ? 1.0 : 0.5;
                    }

                    var btnLinear = shell.FindByName<Button>("MeasureLinearButton");
                    if (btnLinear != null)
                    {
                        bool linEnabled = mode == ShellButtonsMode.Full;
                        btnLinear.IsEnabled = linEnabled;
                        btnLinear.Opacity = linEnabled ? 1.0 : 0.5;
                    }

                    var btnZoomOut = shell.FindByName<Button>("ZoomOutButton");
                    if (btnZoomOut != null)
                    {
                        bool zoomEnabled = mode != ShellButtonsMode.None;
                        btnZoomOut.IsEnabled = zoomEnabled;
                        btnZoomOut.Opacity = zoomEnabled ? 1.0 : 0.5;
                    }

                    var btnToggleDim = shell.FindByName<Button>("ToggleDimensionsButton");
                    if (btnToggleDim != null)
                    {
                        bool togEnabled = mode == ShellButtonsMode.Full;
                        btnToggleDim.IsEnabled = togEnabled;
                        btnToggleDim.Opacity = togEnabled ? 1.0 : 0.5;
                    }

                    // Also hide/disable cycle snap buttons when measurement is not fully enabled
                    var cycleP1 = shell.FindByName<Button>("CycleSnapPoint1Button");
                    var cycleP2 = shell.FindByName<Button>("CycleSnapPoint2Button");
                    bool showSnap = mode == ShellButtonsMode.Full && _drawable.MeasurementMode != MeasurementType.None && _model != null;
                    if (cycleP1 != null)
                    {
                        cycleP1.IsVisible = showSnap;
                        cycleP1.IsEnabled = showSnap && _drawable.MeasurePoint1.HasValue;
                        cycleP1.Opacity = cycleP1.IsEnabled ? 1.0 : 0.5;
                    }
                    if (cycleP2 != null)
                    {
                        cycleP2.IsVisible = showSnap;
                        cycleP2.IsEnabled = showSnap && _drawable.MeasurePoint2.HasValue;
                        cycleP2.Opacity = cycleP2.IsEnabled ? 1.0 : 0.5;
                    }
                }
            }
            catch { }

            // Update page-local buttons
            bool showSnapLocal = mode == ShellButtonsMode.Full && _drawable.MeasurementMode != MeasurementType.None && _model != null;
            CycleSnapPoint1Button.IsVisible = showSnapLocal;
            CycleSnapPoint1Button.IsEnabled = showSnapLocal && _drawable.MeasurePoint1.HasValue;
            CycleSnapPoint1Button.Opacity = CycleSnapPoint1Button.IsEnabled ? 1.0 : 0.5;
            CycleSnapPoint2Button.IsVisible = showSnapLocal;
            CycleSnapPoint2Button.IsEnabled = showSnapLocal && _drawable.MeasurePoint2.HasValue;
            CycleSnapPoint2Button.Opacity = CycleSnapPoint2Button.IsEnabled ? 1.0 : 0.5;
        }

        static async Task<byte[]?> RenderPdfFirstPageAsync(string pdfPath, int scaleFactor = 2)
        {
#if ANDROID
            try
            {
                return await Task.Run(() =>
                {
                    string? tmpPath = null;
                    try
                    {
                        Android.OS.ParcelFileDescriptor? pfd = null;

                        // Approach 1: open the original file directly via Java API.
                        // With MANAGE_EXTERNAL_STORAGE this works on SD card without any copy.
                        try
                        {
                            var jf = new Java.IO.File(pdfPath);
                            if (jf.Exists() && jf.CanRead())
                                pfd = Android.OS.ParcelFileDescriptor.Open(jf, Android.OS.ParcelFileMode.ReadOnly);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PDF View] Direct open failed: {ex.Message}");
                            pfd = null;
                        }

                        // Approach 2: stream-copy to app cache, then open from cache.
                        if (pfd == null)
                        {
                            var cacheDir = Android.App.Application.Context.CacheDir?.AbsolutePath
                                           ?? System.IO.Path.GetTempPath();
                            tmpPath = System.IO.Path.Combine(cacheDir, "pdf_view_" + Guid.NewGuid().ToString("N") + ".pdf");
                            using (var src = System.IO.File.OpenRead(pdfPath))
                            using (var dst = System.IO.File.Create(tmpPath))
                                src.CopyTo(dst);
                            pfd = Android.OS.ParcelFileDescriptor.Open(
                                new Java.IO.File(tmpPath), Android.OS.ParcelFileMode.ReadOnly);
                        }

                        if (pfd == null) return null;

                        try
                        {
                            using var renderer = new Android.Graphics.Pdf.PdfRenderer(pfd);
                            if (renderer.PageCount == 0) return null;
                            using var page = renderer.OpenPage(0);
                            int w = page.Width * scaleFactor;
                            int h = page.Height * scaleFactor;
                            if (w <= 0 || h <= 0) return null;
                            var bmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!)
                                      ?? throw new InvalidOperationException("CreateBitmap returned null");
                            bmp.EraseColor(Android.Graphics.Color.White);
                            page.Render(bmp, null, null, Android.Graphics.Pdf.PdfRenderMode.ForDisplay);
                            using var ms = new MemoryStream();
                            bmp.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, ms);
                            bmp.Recycle();
                            var result = ms.ToArray();
                            return result.Length > 0 ? result : null;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PDF View] Render: {ex.GetType().Name}: {ex.Message}");
                            return null;
                        }
                        finally { try { pfd.Close(); } catch { } }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PDF View] ERROR: {ex.GetType().Name}: {ex.Message}");
                        return null;
                    }
                    finally
                    {
                        if (tmpPath != null) try { System.IO.File.Delete(tmpPath); } catch { }
                    }
                });
            }
            catch { return null; }
#else
            await Task.CompletedTask;
            return null;
#endif
        }

        static async Task OpenWithExternalAppAsync(string filePath, string mimeType)
        {
#if ANDROID
            try
            {
                var context = Android.App.Application.Context;
                var file = new Java.IO.File(filePath);
                // Use FileProvider to get a safe content:// URI
                Android.Net.Uri? uri;
                try
                {
                    uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                        context,
                        context.PackageName + ".fileprovider",
                        file);
                }
                catch
                {
                    // Fallback to file:// URI if FileProvider not available
                    uri = Android.Net.Uri.FromFile(file);
                }
                if (uri == null) return;

                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
                intent.SetDataAndType(uri, mimeType);
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);

                // Exclude our own package from the chooser to avoid infinite loop
                var ownPackage = context.PackageName;
                var pm = context.PackageManager;
                var resolveList = pm?.QueryIntentActivities(intent, Android.Content.PM.PackageInfoFlags.MatchDefaultOnly);
                var excludeComponents = new List<Android.Content.ComponentName>();
                if (resolveList != null && ownPackage != null)
                {
                    foreach (var info in resolveList)
                    {
                        var actInfo = info.ActivityInfo;
                        if (actInfo?.PackageName == ownPackage)
                        {
                            excludeComponents.Add(new Android.Content.ComponentName(actInfo.PackageName, actInfo.Name!));
                        }
                    }
                }

                var chooser = Android.Content.Intent.CreateChooser(intent, "Ouvrir avec");
                if (chooser != null)
                {
                    chooser.AddFlags(Android.Content.ActivityFlags.NewTask);
                    if (excludeComponents.Count > 0)
                    {
                        chooser.PutExtra(Android.Content.Intent.ExtraExcludeComponents,
                            excludeComponents.ToArray());
                    }
                    context.StartActivity(chooser);
                }
            }
            catch { }
            await Task.CompletedTask;
#else
            await Task.CompletedTask;
#endif
        }
    }

    enum MeasurementType
    {
        None,
        Linear,
        Double
    }

    class TagDrawable : IDrawable
    {
        public TagModel? Model { get; set; }
        public float PanX { get; set; }
        public float PanY { get; set; }
        public float Scale { get; set; } = 1f;
        // afficher/masquer la géométrie de la pièce (les cotations restent toujours visibles)
        public bool ShowGeometry { get; set; } = true;
        // optional focus indicator
        public bool ShowFocus { get; set; } = false;
        public double FocusModelX { get; set; }
        public double FocusModelY { get; set; }
        // small axes marker (drawn in model coordinates so it follows zoom)
        public bool ShowAxes { get; set; } = false;
        // measurement mode
        public MeasurementType MeasurementMode { get; set; } = MeasurementType.None;
        public PointF? MeasurePoint1 { get; set; }
        public PointF? MeasurePoint2 { get; set; }

        public void ResetTransform()
        {
            PanX = PanY = 0;
            Scale = 1f;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(dirtyRect);

            // Keep track of reserved rectangles (point markers, snap squares and drawn labels)
            var drawnLabelRects = new List<RectF>();

            if (Model == null)
                return;

            // compute scale to fit
            var width = Model.MaxX - Model.MinX;
            var height = Model.MaxY - Model.MinY;
            if (width <= 0) width = 1;
            if (height <= 0) height = 1;

            float sx = dirtyRect.Width / (float)width;
            float sy = dirtyRect.Height / (float)height;
            float s = Math.Min(sx, sy) * 0.9f; // padding

            canvas.SaveState();
            // translate to center
            canvas.Translate(dirtyRect.Center.X + PanX, dirtyRect.Center.Y + PanY);
            // apply user scale as well as fit-to-view scale
            canvas.Scale(s * Scale, -s * Scale); // flip Y because CAD coordinates usually up
            canvas.Translate((float)(-(Model.MinX + Model.MaxX) / 2.0), (float)(-(Model.MinY + Model.MaxY) / 2.0));

            // draw entities respecting layers
            // choose a stroke size that remains visible on screen (at least 1 device pixel)
            float strokeSize = Math.Max(1f / (s * Scale), 1f);
            canvas.StrokeSize = strokeSize;
            float totalScale = s * Scale;
            float modelCenterX = (float)((Model.MinX + Model.MaxX) / 2.0);
            float modelCenterY = (float)((Model.MinY + Model.MaxY) / 2.0);
            // Track occupied text rectangles in MODEL coordinates to avoid overlaps.
            // Model-space collision is zoom-independent: if two texts overlap in model
            // space they overlap on screen at every zoom level, and vice versa.
            var drawnTextRects = new List<RectF>();

            // Pre-build layer dictionary for O(1) lookup per entity
            var layerDict = Model.LayerDict;

            // Compute visible model-space bounding box for view frustum culling
            float invScale = 1f / (s * Scale);
            float visCX = (float)((Model.MinX + Model.MaxX) / 2.0);
            float visCY = (float)((Model.MinY + Model.MaxY) / 2.0);
            float halfW = dirtyRect.Width * 0.5f * invScale;
            float halfH = dirtyRect.Height * 0.5f * invScale;
            float visMinX = visCX - PanX * invScale - halfW;
            float visMaxX = visCX - PanX * invScale + halfW;
            float visMinY = visCY + PanY * invScale - halfH;
            float visMaxY = visCY + PanY * invScale + halfH;
            // Add margin so entities at the edge are not clipped
            float cullMargin = 20f * invScale;
            visMinX -= cullMargin; visMaxX += cullMargin;
            visMinY -= cullMargin; visMaxY += cullMargin;

            // Cache dash pattern to avoid per-entity allocation
            float modelPerPx = 1f / Math.Max(s * Scale, 0.001f);
            float[] dashedPattern = new float[] { 10f * modelPerPx, 5f * modelPerPx };

            foreach (var ent in Model.Entities)
            {
                // determine color/visibility from layer if possible (use original colors)
                Color entColor = Colors.Black;
                bool visible = true;
                int lineType = 0;
                if (layerDict.TryGetValue(ent.LayerId, out var lay))
                {
                    visible = lay.Visible;
                    entColor = lay.Color;
                    lineType = lay.LineType;
                    // if the layer color is white (or nearly white) render as black for visibility
                    if (entColor.Red >= 0.95f && entColor.Green >= 0.95f && entColor.Blue >= 0.95f)
                        entColor = Colors.Black;
                }
                // per-entity lineType overrides layer lineType
                if (ent.LineType >= 2)
                    lineType = ent.LineType;

                if (!visible) continue;

                // View frustum culling: skip entities entirely outside visible area
                switch (ent)
                {
                    case TagLine cl:
                        float lMinX = Math.Min(cl.X1, cl.X2);
                        float lMaxX = Math.Max(cl.X1, cl.X2);
                        float lMinY = Math.Min(cl.Y1, cl.Y2);
                        float lMaxY = Math.Max(cl.Y1, cl.Y2);
                        if (lMaxX < visMinX || lMinX > visMaxX || lMaxY < visMinY || lMinY > visMaxY)
                            continue;
                        break;
                    case TagCircle cc:
                        if (cc.CX + cc.R < visMinX || cc.CX - cc.R > visMaxX || cc.CY + cc.R < visMinY || cc.CY - cc.R > visMaxY)
                            continue;
                        break;
                    case TagArc ca:
                        if (ca.CX + ca.R < visMinX || ca.CX - ca.R > visMaxX || ca.CY + ca.R < visMinY || ca.CY - ca.R > visMaxY)
                            continue;
                        break;
                    case TagPoint cp:
                        if (cp.X < visMinX || cp.X > visMaxX || cp.Y < visMinY || cp.Y > visMaxY)
                            continue;
                        break;
                    case TagText ct:
                        // approximate text bounding: position + some extent
                        float textExtent = ct.Size * ct.Text.Length * 0.7f;
                        if (ct.X + textExtent < visMinX || ct.X > visMaxX || ct.Y - ct.Size < visMinY || ct.Y + ct.Size > visMaxY)
                            continue;
                        break;
                }

                // Dimension lines and text are rendered in red; everything else in black
                // Detection uses majority-color heuristic: entities whose ColorIndex differs
                // from the majority geometry color are dimension/annotation entities.
                // TagText entities are always treated as dimension.
                // Dashed/hidden entities (lineType != 0) are kept black to preserve hidden lines.
                bool isDimensionEntity = false;
                if (ent is TagText)
                    isDimensionEntity = true;
                else if (lineType == 0 && ent.ColorIndex >= 0 && Model.DimensionColorIndices.Contains(ent.ColorIndex))
                    isDimensionEntity = true;
                else if (lineType == 0)
                {
                    // Reuse the same layer from dict (no second lookup needed)
                    if (lay != null && lay.IsDimension)
                        isDimensionEntity = true;
                }
                entColor = isDimensionEntity ? Colors.Red : Colors.Black;

                // Masquer la géométrie de la pièce si désactivé (les cotations restent visibles)
                if (!isDimensionEntity && !ShowGeometry)
                    continue;

                // Apply dashed stroke pattern for hidden/dashed line types (traits interrompus)
                if (lineType != 0)
                {
                    canvas.StrokeDashPattern = dashedPattern;
                }
                else
                {
                    canvas.StrokeDashPattern = null;
                }

                switch (ent)
                {
                    case TagLine l:
                        canvas.StrokeColor = entColor;
                        canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2);
                        break;
                    case TagPoint p:
                        canvas.FillColor = entColor;
                        float r = 2f / s;
                        canvas.FillCircle(p.X, p.Y, r);
                        break;
                    case TagCircle c:
                        // use layer color for circle stroke and ensure visible stroke size
                        canvas.StrokeColor = entColor;
                        canvas.StrokeSize = strokeSize;
                        // draw circle with proper radius in model coordinates
                        canvas.DrawCircle(c.CX, c.CY, c.R);
                        break;
                    case TagArc a:
                        canvas.StrokeColor = entColor;
                        canvas.StrokeSize = strokeSize;
                        double startRad = a.StartAngle * Math.PI / 180.0;
                        double sweepRad = a.SweepAngle * Math.PI / 180.0;
                        sweepRad = ((sweepRad % (2*Math.PI)) + 2*Math.PI) % (2*Math.PI);
                        if (sweepRad <= 0) sweepRad += 2*Math.PI;
                        int segs = Math.Max(6, (int)Math.Ceiling(Math.Abs(sweepRad) / (2*Math.PI) * 128));
                        double step = sweepRad / segs;
                        // draw arc as a single path for proper dash pattern rendering
                        double ang0 = startRad;
                        var arcPath = new PathF();
                        float apx = a.CX + a.R * (float)Math.Cos(ang0);
                        float apy = a.CY + a.R * (float)Math.Sin(ang0);
                        arcPath.MoveTo(apx, apy);
                        for (int i = 1; i <= segs; i++)
                        {
                            double ang = startRad + step * i;
                            float nx = a.CX + a.R * (float)Math.Cos(ang);
                            float ny = a.CY + a.R * (float)Math.Sin(ang);
                            arcPath.LineTo(nx, ny);
                        }
                        canvas.DrawPath(arcPath);
                        break;
                    case TagText t:
                        canvas.FillColor = entColor;
                        canvas.FontColor = entColor;
                        canvas.StrokeColor = entColor;
                        canvas.FontSize = t.Size;
                        var lines = (t.Text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        float lineHeight = t.Size * 1.2f;
                        float tAscent = t.Size * 0.8f;
                        float tDescent = t.Size * 0.25f;
                        float tMargin = t.Size * 0.10f;
                        // Per-line collision detection in model coordinates.
                        // Two multi-line entities may not overlap as a whole but their
                        // individual lines at the same Y level can visually overlap.
                        // Lines within the SAME TagText entity must not collide with each
                        // other: collect their rects first, check only against OTHER entities,
                        // then add them all after the loop.
                        canvas.SaveState();
                        canvas.FontColor = entColor;
                        canvas.FillColor = entColor;
                        canvas.Translate(t.X, t.Y);
                        canvas.Scale(1, -1);
                        canvas.FontSize = t.Size;
                        var pendingRects = new List<RectF>();
                        for (int li = 0; li < lines.Length; li++)
                        {
                            // Model Y of this line's baseline (after Scale(1,-1), line i is at t.Y − i*lineHeight)
                            float lineModelY = t.Y - li * lineHeight;
                            float lineW = lines[li].Length * t.Size * 0.7f;
                            var lineRect = new RectF(
                                t.X - tMargin,
                                lineModelY - tDescent - tMargin,
                                lineW + tMargin * 2f,
                                tAscent + tDescent + tMargin * 2f);
                            // Check if this individual line overlaps any previously drawn line from OTHER entities
                            bool lineOverlap = false;
                            foreach (var existing in drawnTextRects)
                            {
                                if (!(lineRect.X + lineRect.Width <= existing.X ||
                                      existing.X + existing.Width <= lineRect.X ||
                                      lineRect.Y + lineRect.Height <= existing.Y ||
                                      existing.Y + existing.Height <= lineRect.Y))
                                {
                                    lineOverlap = true;
                                    break;
                                }
                            }
                            if (!lineOverlap)
                            {
                                pendingRects.Add(lineRect);
                                canvas.DrawString(lines[li], 0, li * lineHeight, HorizontalAlignment.Left);
                            }
                        }
                        drawnTextRects.AddRange(pendingRects);
                        canvas.RestoreState();
                        break;
                }
            }

            // Reset dash pattern before restoring state so subsequent drawing is not dashed
            canvas.StrokeDashPattern = null;
            canvas.RestoreState();

            // Draw small axes marker anchored top-right of the viewport (fixed in screen coordinates)
            if (ShowAxes)
            {
                try
                {
                    // anchor in device coordinates: top-right corner with a margin
                    float margin = Math.Min(40f, dirtyRect.Width * 0.08f);
                    float axisLen = Math.Max(30f, Math.Min(dirtyRect.Width, dirtyRect.Height) * 0.06f);
                    float ox = dirtyRect.Right - margin;
                    float oy = dirtyRect.Top + margin;

                    canvas.SaveState();
                    canvas.StrokeSize = 3f;
                    // X axis (red) pointing left so it stays inside the view
                    canvas.StrokeColor = Colors.Red;
                    canvas.DrawLine(ox, oy, ox - axisLen, oy);
                    // Y axis (green) pointing down so it stays inside the view
                    canvas.StrokeColor = Colors.Green;
                    canvas.DrawLine(ox, oy, ox, oy + axisLen);

                    // small filled marker for visibility
                    canvas.FillColor = Colors.Yellow;
                    float centerMarkR = Math.Max(4f, axisLen * 0.06f);
                    canvas.FillCircle(ox, oy, centerMarkR);
                    canvas.StrokeColor = Colors.Black;
                    canvas.StrokeSize = 1f;
                    canvas.DrawCircle(ox, oy, centerMarkR);

                    // labels (placed near axis ends)
                    canvas.FontSize = Math.Max(12, axisLen * 0.18f);
                    canvas.FontColor = Colors.Red;
                    canvas.DrawString("X", ox - axisLen - 8, oy - 6, HorizontalAlignment.Left);
                    canvas.FontColor = Colors.Green;
                    canvas.DrawString("Y", ox + 6, oy + axisLen + 2, HorizontalAlignment.Left);
                    canvas.RestoreState();
                }
                catch { }
            }

            // Draw measurement indicators and distance if in measurement mode
            if (MeasurementMode != MeasurementType.None && MeasurePoint1.HasValue)
            {
                // Points are stored in model coordinates, need to convert to screen for drawing
                float centerX = dirtyRect.Center.X;
                float centerY = dirtyRect.Center.Y;

                // Point 1 in screen coords
                float screen1X = centerX + PanX + (MeasurePoint1.Value.X - modelCenterX) * totalScale;
                float screen1Y = centerY + PanY - (MeasurePoint1.Value.Y - modelCenterY) * totalScale;

                canvas.SaveState();
                // P1 = Fuchsia, P2 = Violet
                Color p1Color = Colors.Brown;
                Color p2Color = Colors.Gray;
                canvas.FillColor = p1Color;
                canvas.StrokeColor = p1Color;
                canvas.FontColor = p1Color;
                canvas.FontSize = 16;

                // Draw first point indicator
                canvas.FillCircle(screen1X, screen1Y, 5);
                PlaceLabel(canvas, "1", screen1X + 4, screen1Y - 4, 16, drawnLabelRects, HorizontalAlignment.Left);

                // Draw snap indicator around point 1
                canvas.StrokeColor = p1Color;
                canvas.StrokeSize = 3;
                canvas.DrawRectangle(screen1X - 12, screen1Y - 12, 24, 24);

                // Reserve rects for the point indicator and its snap square so labels won't overlap them
                drawnLabelRects.Add(new RectF(screen1X - 12, screen1Y - 12, 24, 24));
                drawnLabelRects.Add(new RectF(screen1X - 5, screen1Y - 5, 10, 10));

                // If only the first measurement point exists, show radius immediately when it snaps to a circle/arc
                try
                {
                    if (!MeasurePoint2.HasValue && Model != null)
                    {
                        foreach (var entity2 in Model.Entities)
                        {
                            if (entity2 is TagCircle c2)
                            {
                                const float EPS = 0.01f;
                                if (Math.Abs(c2.CX - MeasurePoint1.Value.X) < EPS && Math.Abs(c2.CY - MeasurePoint1.Value.Y) < EPS)
                                                {
                                                    canvas.FontSize = 16;
                                                    canvas.FontColor = p1Color;
                                                    PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                                    break;
                                                }
                                                // on circumference
                                                float ddx = MeasurePoint1.Value.X - c2.CX;
                                                float ddy = MeasurePoint1.Value.Y - c2.CY;
                                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                                if (Math.Abs(dd - c2.R) < Math.Max(0.1f, c2.R * 0.01f))
                                                {
                                                    canvas.FontSize = 16;
                                                    canvas.FontColor = p1Color;
                                                    PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                                    break;
                                                }
                                            }
                                            else if (entity2 is TagArc a2)
                                            {
                                                const float EPS = 0.01f;
                                                if (Math.Abs(a2.CX - MeasurePoint1.Value.X) < EPS && Math.Abs(a2.CY - MeasurePoint1.Value.Y) < EPS)
                                                {
                                                    canvas.FontSize = 16;
                                                    canvas.FontColor = p1Color;
                                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                                    break;
                                                }
                                                float ddx = MeasurePoint1.Value.X - a2.CX;
                                                float ddy = MeasurePoint1.Value.Y - a2.CY;
                                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                                if (Math.Abs(dd - a2.R) < Math.Max(0.1f, a2.R * 0.01f))
                                                {
                                                    canvas.FontSize = 16;
                                                    canvas.FontColor = p1Color;
                                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }

                if (MeasurePoint2.HasValue)
                {
                    // Point 2 in screen coords
                    float screen2X = centerX + PanX + (MeasurePoint2.Value.X - (float)modelCenterX) * s * Scale;
                    float screen2Y = centerY + PanY - (MeasurePoint2.Value.Y - (float)modelCenterY) * s * Scale;

                    // Draw second point indicator
                    canvas.FillColor = p2Color;
                    canvas.FontColor = p2Color;
                    canvas.FillCircle(screen2X, screen2Y, 5);
                    PlaceLabel(canvas, "2", screen2X + 4, screen2Y - 4, 16, drawnLabelRects, HorizontalAlignment.Left);

                    // Draw snap indicator around point 2
                    canvas.StrokeColor = p2Color;
                    canvas.StrokeSize = 3;
                    canvas.DrawRectangle(screen2X - 12, screen2Y - 12, 24, 24);

                    // Reserve rects for the second point indicator and its snap square so labels won't overlap them
                    drawnLabelRects.Add(new RectF(screen2X - 12, screen2Y - 12, 24, 24));
                    drawnLabelRects.Add(new RectF(screen2X - 5, screen2Y - 5, 10, 10));

                    // Calculate distances in model coordinates (mm)
                    float dx = MeasurePoint2.Value.X - MeasurePoint1.Value.X;
                    float dy = MeasurePoint2.Value.Y - MeasurePoint1.Value.Y;
                    float distanceMM = (float)Math.Sqrt(dx * dx + dy * dy);

                    // If one of the measurement points is on/center of a circle/arc, show its radius
                    bool radiusShown = false;
                    if (MeasurePoint1.HasValue && Model != null)
                    {
                        // check model entities for circle/arc containing or centered at this point
                        foreach (var entity2 in Model.Entities)
                        {
                            if (entity2 is TagCircle c2)
                            {
                                const float EPS = 0.01f;
                                if (Math.Abs(c2.CX - MeasurePoint1.Value.X) < EPS && Math.Abs(c2.CY - MeasurePoint1.Value.Y) < EPS)
                                {
                                canvas.FontSize = 16;
                                canvas.FontColor = p1Color;
                                PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                                // on circumference
                                float ddx = MeasurePoint1.Value.X - c2.CX;
                                float ddy = MeasurePoint1.Value.Y - c2.CY;
                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                if (Math.Abs(dd - c2.R) < Math.Max(0.1f, c2.R * 0.01f))
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p1Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                            }
                            else if (entity2 is TagArc a2)
                            {
                                const float EPS = 0.01f;
                                if (Math.Abs(a2.CX - MeasurePoint1.Value.X) < EPS && Math.Abs(a2.CY - MeasurePoint1.Value.Y) < EPS)
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p1Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                                float ddx = MeasurePoint1.Value.X - a2.CX;
                                float ddy = MeasurePoint1.Value.Y - a2.CY;
                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                if (Math.Abs(dd - a2.R) < Math.Max(0.1f, a2.R * 0.01f))
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p1Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen1X, screen1Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!radiusShown && MeasurePoint2.HasValue && Model != null)
                    {
                        foreach (var entity2 in Model.Entities)
                        {
                            if (entity2 is TagCircle c2)
                            {
                                const float EPS = 0.01f;
                                if (Math.Abs(c2.CX - MeasurePoint2.Value.X) < EPS && Math.Abs(c2.CY - MeasurePoint2.Value.Y) < EPS)
                                {
                                canvas.FontSize = 16;
                                canvas.FontColor = p2Color;
                                PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen2X, screen2Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                                float ddx = MeasurePoint2.Value.X - c2.CX;
                                float ddy = MeasurePoint2.Value.Y - c2.CY;
                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                if (Math.Abs(dd - c2.R) < Math.Max(0.1f, c2.R * 0.01f))
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p2Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(c2.R * 2)}", screen2X, screen2Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                            }
                            else if (entity2 is TagArc a2)
                            {
                                const float EPS = 0.01f;
                                if (Math.Abs(a2.CX - MeasurePoint2.Value.X) < EPS && Math.Abs(a2.CY - MeasurePoint2.Value.Y) < EPS)
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p2Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen2X, screen2Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                                float ddx = MeasurePoint2.Value.X - a2.CX;
                                float ddy = MeasurePoint2.Value.Y - a2.CY;
                                float dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                                if (Math.Abs(dd - a2.R) < Math.Max(0.1f, a2.R * 0.01f))
                                {
                                    canvas.FontSize = 16;
                                    canvas.FontColor = p2Color;
                                    PlaceLabel(canvas, $"Ø: {Fmt(a2.R * 2)}", screen2X, screen2Y - 30, 16, drawnLabelRects, HorizontalAlignment.Center);
                                    radiusShown = true;
                                    break;
                                }
                            }
                        }
                    }
                    float distanceX = Math.Abs(dx);
                    float distanceY = Math.Abs(dy);

                    // Draw based on measurement type
                    // Offset (in screen pixels) so dimension lines don't sit on top of drawing lines
                    const float DIM_OFFSET = 25f;
                    const float EXT_GAP = 4f; // small gap between geometry and extension line

                    if (MeasurementMode == MeasurementType.Linear)
                    {
                        // Perpendicular offset so the dimension line is parallel but away from the geometry
                        float ldx = screen2X - screen1X;
                        float ldy = screen2Y - screen1Y;
                        float lineLen = (float)Math.Sqrt(ldx * ldx + ldy * ldy);
                        if (lineLen < 0.001f) lineLen = 1f;
                        // perpendicular unit vector (pointing "left" of direction)
                        float perpX = -ldy / lineLen;
                        float perpY = ldx / lineLen;

                        // offset endpoints
                        float off1X = screen1X + perpX * DIM_OFFSET;
                        float off1Y = screen1Y + perpY * DIM_OFFSET;
                        float off2X = screen2X + perpX * DIM_OFFSET;
                        float off2Y = screen2Y + perpY * DIM_OFFSET;

                        // Extension lines (thin, from near the original point to the offset point)
                        canvas.StrokeColor = Colors.Green;
                        canvas.StrokeSize = 1;
                        canvas.DrawLine(screen1X + perpX * EXT_GAP, screen1Y + perpY * EXT_GAP,
                                        off1X + perpX * 4f, off1Y + perpY * 4f);
                        canvas.DrawLine(screen2X + perpX * EXT_GAP, screen2Y + perpY * EXT_GAP,
                                        off2X + perpX * 4f, off2Y + perpY * 4f);

                        // Dimension line (offset)
                        canvas.StrokeColor = Colors.Green;
                        canvas.StrokeSize = 2;
                        canvas.DrawLine(off1X, off1Y, off2X, off2Y);

                        // Display distance at midpoint of offset line
                        float midX = (off1X + off2X) / 2;
                        float midY = (off1Y + off2Y) / 2;
                        canvas.FontColor = Colors.Green;
                        PlaceLabel(canvas, $"{Fmt(distanceMM)}", midX, midY - 20, 18, drawnLabelRects, HorizontalAlignment.Center);
                    }
                    else if (MeasurementMode == MeasurementType.Double)
                    {
                        // Orthogonal measurement: offset X and Y dimension lines away from geometry
                        canvas.StrokeSize = 2;

                        // Determine offset directions to push lines away from the geometry
                        float hOffsetDir = (screen2Y > screen1Y) ? -1f : 1f; // horizontal line offset direction (vertical push)
                        float vOffsetDir = (screen1X < screen2X) ? 1f : -1f; // vertical line offset direction (horizontal push)

                        float hLineY = screen1Y + hOffsetDir * DIM_OFFSET; // Y of offset horizontal dim line
                        float vLineX = screen2X + vOffsetDir * DIM_OFFSET; // X of offset vertical dim line

                        // Extension lines for horizontal dimension (thin)
                        canvas.StrokeColor = Colors.Blue;
                        canvas.StrokeSize = 1;
                        canvas.DrawLine(screen1X, screen1Y + hOffsetDir * EXT_GAP, screen1X, hLineY + hOffsetDir * 4f);
                        canvas.DrawLine(screen2X, screen1Y + hOffsetDir * EXT_GAP, screen2X, hLineY + hOffsetDir * 4f);

                        // Horizontal dimension line (X distance) - offset
                        canvas.StrokeColor = Colors.Blue;
                        canvas.StrokeSize = 2;
                        canvas.DrawLine(screen1X, hLineY, screen2X, hLineY);
                        float midXHoriz = (screen1X + screen2X) / 2;
                        canvas.FontSize = 16;
                        canvas.FontColor = Colors.Blue;
                        PlaceLabel(canvas, $"X: {Fmt(distanceX)}", midXHoriz, hLineY + hOffsetDir * (-18), 16, drawnLabelRects, HorizontalAlignment.Center);

                        // Extension lines for vertical dimension (thin)
                        canvas.StrokeColor = Colors.Blue;
                        canvas.StrokeSize = 1;
                        canvas.DrawLine(screen2X + vOffsetDir * EXT_GAP, screen1Y, vLineX + vOffsetDir * 4f, screen1Y);
                        canvas.DrawLine(screen2X + vOffsetDir * EXT_GAP, screen2Y, vLineX + vOffsetDir * 4f, screen2Y);

                        // Vertical dimension line (Y distance) - offset
                        canvas.StrokeColor = Colors.Blue;
                        canvas.StrokeSize = 2;
                        canvas.DrawLine(vLineX, screen1Y, vLineX, screen2Y);
                        float midYVert = (screen1Y + screen2Y) / 2;
                        PlaceLabel(canvas, $"Y: {Fmt(distanceY)}", vLineX + vOffsetDir * 15, midYVert, 16, drawnLabelRects, HorizontalAlignment.Left);
                    }
                }

                canvas.RestoreState();
            }
        }

        static string Fmt(float v)
        {
            if (v == (int)v) return ((int)v).ToString();
            string s = v.ToString("F2");
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }

        // Helper to draw a label while avoiding overlaps with reserved rectangles. It will try several offsets.
        void PlaceLabel(ICanvas canvas, string text, float x, float y, float fontSize, List<RectF> reserved, HorizontalAlignment halign)
        {
            canvas.FontSize = fontSize;
            // approximate text size (width approx 0.6 * fontSize * chars, height = fontSize)
            float textW = 0.6f * fontSize * text.Length;
            float textH = fontSize;

            // candidate offsets (dx, dy)
            var candidates = new List<(float dx, float dy)>()
            {
                (0, -textH - 4), // above
                (6, -textH - 4), // above-right
                (6, 0), // right
                (6, textH + 4), // below-right
                (0, textH + 4), // below
                (-textW - 6, 0), // left
                (-textW - 6, -textH - 4), // above-left
                (-textW - 6, textH + 4) // below-left
            };

            RectF chosen = new RectF(x, y - textH, textW, textH);
            bool placed = false;
            foreach (var c in candidates)
            {
                var r = new RectF(x + c.dx, y + c.dy - textH, textW, textH);
                bool coll = false;
                foreach (var rr in reserved)
                {
                    // manual rect intersection test
                    if (!(r.X + r.Width <= rr.X || rr.X + rr.Width <= r.X || r.Y + r.Height <= rr.Y || rr.Y + rr.Height <= r.Y))
                    {
                        coll = true; break;
                    }
                }
                if (!coll)
                {
                    chosen = r;
                    placed = true;
                    break;
                }
            }

            // reserve chosen rect
            reserved.Add(chosen);

            // draw text according to halign inside chosen
            float drawX = chosen.X;
            if (halign == HorizontalAlignment.Center)
                drawX = chosen.X + chosen.Width / 2f;
            else if (halign == HorizontalAlignment.Left)
                drawX = chosen.X;

            canvas.DrawString(text, drawX, chosen.Y, halign);
        }
    }
}
