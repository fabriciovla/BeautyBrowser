using CefSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BeautyKit;

// The context menu, with Chrome's entries for whatever was clicked.
public sealed partial class BrowserView
{
    private async void ShowContextMenu(ContextMenuInfo info)
    {
        if (_tab is not { } tab) return;

        // CEF does not give the link text, the video state or always the selection inside a link: ask the page.
        if (info.HasLink || info.IsVideo)
        {
            info = await tab.ReadPointAsync(info);
            if (_tab != tab) return; // another tab was selected meanwhile
        }

        var menu = new MenuFlyout();
        var items = menu.Items;

        void Add(string text, Action action, bool enabled = true, string? shortcut = null)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += (_, _) => action();
            items.Add(item);
        }

        void AddToggle(string text, bool isChecked, Action action)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        void Separator()
        {
            if (items.Count > 0 && items[^1] is not MenuFlyoutSeparator) items.Add(new MenuFlyoutSeparator());
        }

        if (info.IsEditable && !string.IsNullOrEmpty(info.MisspelledWord))
        {
            if (info.Suggestions.Count == 0)
                Add(L.T("No spelling suggestions", "Sin sugerencias de ortografía"), () => { }, enabled: false);
            foreach (var suggestion in info.Suggestions.Take(5))
                Add(suggestion, () => tab.ReplaceMisspelling(suggestion));
            Separator();
        }

        if (info.HasLink)
        {
            Add(L.T("Open link in new tab", "Abrir enlace en una pestaña nueva"), () => tab.OpenInNewTab(info.LinkUrl));
            Add(L.T("Save link as…", "Guardar enlace como…"), () => tab.Download(info.LinkUrl, askWhere: true));
            Add(L.T("Copy link address", "Copiar dirección del enlace"), () => CopyText(info.LinkUrl));
            if (info.LinkText.Length > 0)
                Add(L.T("Copy link text", "Copiar texto del enlace"), () => CopyText(info.LinkText));
            Separator();
        }

        if (info.IsImage)
        {
            Add(L.T("Open image in new tab", "Abrir imagen en una pestaña nueva"), () => tab.OpenInNewTab(info.SourceUrl));
            Add(L.T("Save image as…", "Guardar imagen como…"), () => tab.Download(info.SourceUrl, askWhere: true));
            Add(L.T("Copy image", "Copiar imagen"), () => CopyImage(tab, info), enabled: info.SourceUrl.Length > 0);
            Add(L.T("Copy image address", "Copiar dirección de la imagen"), () => CopyText(info.SourceUrl));
            Separator();
        }

        if (info.IsVideo || info.IsAudio)
        {
            var kind = info.IsVideo ? "video" : "audio";
            if (info.IsVideo)
            {
                AddToggle(L.T("Loop", "Repetir"), info.VideoLoop, () => tab.ToggleVideoLoop(info.X, info.Y));
                AddToggle(L.T("Show controls", "Mostrar controles"), info.VideoControls, () => tab.ToggleVideoControls(info.X, info.Y));
                // Only when the page says Picture in Picture is available (document.pictureInPictureEnabled).
                if (info.CanPictureInPicture)
                    Add(L.T("Picture in picture", "Pantalla en pantalla"), () => tab.TogglePictureInPicture(info.X, info.Y));
                Separator();
            }
            Add(L.T($"Open {kind} in new tab", $"Abrir {kind} en una pestaña nueva"), () => tab.OpenInNewTab(info.SourceUrl));
            Add(L.T($"Save {kind} as…", $"Guardar {kind} como…"), () => tab.Download(info.SourceUrl, askWhere: true));
            Add(L.T($"Copy {kind} address", $"Copiar dirección del {kind}"), () => CopyText(info.SourceUrl));
            Separator();
        }

        if (info.IsEditable)
        {
            Add(L.T("Undo", "Deshacer"), tab.Undo, info.Can(ContextMenuEditState.CanUndo), "Ctrl+Z");
            Add(L.T("Redo", "Rehacer"), tab.Redo, info.Can(ContextMenuEditState.CanRedo), "Ctrl+Y");
            Separator();
            Add(L.T("Cut", "Cortar"), tab.Cut, info.Can(ContextMenuEditState.CanCut), "Ctrl+X");
            Add(L.T("Copy", "Copiar"), tab.Copy, info.Can(ContextMenuEditState.CanCopy), "Ctrl+C");
            Add(L.T("Paste", "Pegar"), tab.Paste, info.Can(ContextMenuEditState.CanPaste), "Ctrl+V");
            Add(L.T("Paste as plain text", "Pegar como texto sin formato"), tab.PasteAsPlainText,
                info.Can(ContextMenuEditState.CanPaste), "Ctrl+Shift+V");
            Add(L.T("Delete", "Eliminar"), tab.Delete, info.Can(ContextMenuEditState.CanDelete));
            Separator();
            Add(L.T("Select all", "Seleccionar todo"), tab.SelectAll, info.Can(ContextMenuEditState.CanSelectAll), "Ctrl+A");
            Separator();
        }
        else if (info.HasSelection)
        {
            Add(L.T("Copy", "Copiar"), tab.Copy, shortcut: "Ctrl+C");
            var query = info.SelectionText.Trim();
            var preview = query.Length > 24 ? query[..24] + "…" : query;
            var engine = StartPage.SearchEngineName;
            Add(L.T($"Search {engine} for \"{preview}\"", $"Buscar \"{preview}\" en {engine}"),
                () => tab.OpenInNewTab(string.Format(BrowserTab.SearchUrlTemplate, Uri.EscapeDataString(query))));
            Separator();
        }

        if (!info.HasLink && !info.IsImage && !info.IsVideo && !info.IsAudio && !info.IsEditable && !info.HasSelection)
        {
            Add(L.T("Back", "Atrás"), tab.GoBack, tab.CanGoBack);
            Add(L.T("Forward", "Adelante"), tab.GoForward, tab.CanGoForward);
            Add(L.T("Reload", "Volver a cargar"), () => tab.Reload(), shortcut: "Ctrl+R");
            Separator();
            Add(L.T("Save as…", "Guardar como…"), () => SavePage(tab));
            Add(L.T("Print…", "Imprimir…"), tab.Print);
            Separator();
            Add(L.T("View page source", "Ver código fuente de la página"), tab.ViewSource);
        }

        Separator();
        Add(L.T("Inspect", "Inspeccionar"), () => tab.Inspect(info.X, info.Y));

        menu.ShowAt(this, new FlyoutShowOptions
        {
            Position = new Point(info.X, info.Y),
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static void CopyImage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromUri(uri));
        Clipboard.SetContent(package);
    }
}
