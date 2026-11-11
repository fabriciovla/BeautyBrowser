using Microsoft.UI.Xaml.Input;

namespace BeautyBrowser;

// Ctrl+Shift+Delete: the browser's, as in Chrome, so pages never get it. It opens the settings at the rows that clear
// history, cookies and cache.
public sealed partial class MainWindow
{
    private void ClearData_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HidePreview();
        if (_fullscreen) _shell.CurrentTab?.ExitFullscreen();
        OpenLibrary(Controls.LibrarySection.Settings);
        Library.RevealClearData();
    }
}
