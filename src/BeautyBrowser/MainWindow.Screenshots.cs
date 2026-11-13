using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BeautyBrowser;

// Screenshots: the whole page (also what is past the window) or an area dragged over the page. They are copied to the
// clipboard and saved in Downloads, and a notice says so (a tap on it shows the file).
public sealed partial class MainWindow
{
    private Point? _captureStart;
    private Rect _captureArea;
    private bool _capturing; // a capture is being taken: another one waits

    private void SetupScreenshots()
    {
        WebView.CaptureRequested += (_, mode) => StartCapture(mode);
        _shell.CurrentTabChanged += (_, _) => CancelCapture();
        CaptureOverlay.SizeChanged += (_, _) => DrawCapture();
    }

    private bool CanCapture(out BrowserTab tab)
    {
        tab = _shell.CurrentTab!;
        if (_shell.CurrentTab is null || _capturing) return false;
        if (SettingsPage.Visibility == Visibility.Visible || _libraryShown || PeekOpen)
        {
            ShowInfoToast(L.T("Open a web page to take a screenshot", "Abrí una página web para sacar una captura"), "", "", null);
            return false;
        }
        return true;
    }

    /// <summary>Ctrl+Shift+S and the page's menu: the whole page right away, or the area picker.</summary>
    private void StartCapture(ScreenshotMode mode)
    {
        if (!CanCapture(out var tab)) return;
        if (mode == ScreenshotMode.FullPage)
        {
            _ = CaptureFullPageAsync(tab);
            return;
        }
        _captureStart = null;
        _captureArea = Rect.Empty;
        CaptureOverlay.Visibility = Visibility.Visible;
        DrawCapture();
        CaptureCancel.Focus(FocusState.Programmatic); // so Esc reaches the app and not the page
    }

    /// <summary>Closes the area picker. True if it was open.</summary>
    private bool CancelCapture()
    {
        if (CaptureOverlay.Visibility != Visibility.Visible) return false;
        CaptureOverlay.Visibility = Visibility.Collapsed;
        _captureStart = null;
        return true;
    }

    private void CaptureOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CaptureOverlay);
        if (point.Properties.IsRightButtonPressed)
        {
            CancelCapture();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        _captureStart = Clamp(point.Position);
        _captureArea = new Rect(_captureStart.Value, new Size(0, 0));
        CaptureOverlay.CapturePointer(e.Pointer);
        DrawCapture();
        e.Handled = true;
    }

    private void CaptureOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_captureStart is not { } start) return;
        var now = Clamp(e.GetCurrentPoint(CaptureOverlay).Position);
        _captureArea = new Rect(new Point(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y)),
            new Point(Math.Max(start.X, now.X), Math.Max(start.Y, now.Y)));
        DrawCapture();
    }

    private void CaptureOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_captureStart is null) return;
        _captureStart = null;
        CaptureOverlay.ReleasePointerCapture(e.Pointer);
        if (_captureArea.Width < 8 || _captureArea.Height < 8)
        {
            _captureArea = Rect.Empty; // a click, not a drag
            DrawCapture();
            return;
        }
        var area = _captureArea;
        var view = new Size(CaptureOverlay.ActualWidth, CaptureOverlay.ActualHeight);
        CancelCapture();
        if (_shell.CurrentTab is { } tab) _ = CaptureAreaAsync(tab, area, view);
    }

    private void CaptureFullPage_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        if (CanCapture(out var tab)) _ = CaptureFullPageAsync(tab);
    }

    private void CaptureCancel_Click(object sender, RoutedEventArgs e) => CancelCapture();

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, CaptureOverlay.ActualWidth), Math.Clamp(p.Y, 0, CaptureOverlay.ActualHeight));

    // The page dims everywhere but inside the chosen area.
    private void DrawCapture()
    {
        var width = CaptureOverlay.ActualWidth;
        var height = CaptureOverlay.ActualHeight;
        var all = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        var hole = !_captureArea.IsEmpty && _captureArea.Width > 0 && _captureArea.Height > 0;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(all);
        if (hole) group.Children.Add(new RectangleGeometry { Rect = _captureArea });
        CaptureDim.Data = group;

        CaptureFrame.Visibility = hole ? Visibility.Visible : Visibility.Collapsed;
        if (!hole) return;
        CaptureFrame.Margin = new Thickness(_captureArea.X, _captureArea.Y, 0, 0);
        CaptureFrame.Width = _captureArea.Width;
        CaptureFrame.Height = _captureArea.Height;
    }

    private async Task CaptureAreaAsync(BrowserTab tab, Rect area, Size view)
    {
        _capturing = true;
        try
        {
            var shot = await tab.CaptureScreenshotAsync();
            if (shot is not null) shot = await ScreenshotService.CropAsync(shot, area, view);
            await FinishCaptureAsync(tab, shot);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task CaptureFullPageAsync(BrowserTab tab)
    {
        _capturing = true;
        try
        {
            ShowInfoToast(L.T("Capturing the page…", "Capturando la página…"), "", "", null);
            var tiles = await tab.CaptureFullPageAsync();
            await FinishCaptureAsync(tab, tiles is null ? null : await ScreenshotService.StitchAsync(tiles));
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task FinishCaptureAsync(BrowserTab tab, byte[]? png)
    {
        if (png is null)
        {
            ShowInfoToast(L.T("The screenshot could not be taken", "No se pudo sacar la captura"), tab.Title, "", null);
            return;
        }
        var (path, copied) = await ScreenshotService.SaveAndCopyAsync(png, tab.Title, _shell.Settings.DownloadsFolder);
        if (path is null)
        {
            ShowInfoToast(copied ? L.T("Screenshot copied (it could not be saved)", "Captura copiada (no se pudo guardar)")
                : L.T("The screenshot could not be saved", "No se pudo guardar la captura"), "", "", null);
            return;
        }
        var what = copied ? L.T("Screenshot copied and saved", "Captura copiada y guardada") : L.T("Screenshot saved", "Captura guardada");
        ShowInfoToast(what, Path.GetFileName(path), "", () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch { /* the shell is not available: nothing to show */ }
        });
    }
}
