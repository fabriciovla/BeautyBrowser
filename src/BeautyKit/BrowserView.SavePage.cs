using Microsoft.UI;
using Windows.Storage.Pickers;

namespace BeautyKit;

// "Save as…" in the context menu: the whole page as one MHTML file, or the file itself when the tab shows one (a PDF).
public sealed partial class BrowserView
{
    private static readonly string[] FileExtensions =
        [".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".mp4", ".webm", ".mp3", ".ogg", ".wav", ".zip", ".txt", ".json", ".xml"];

    private static bool ShowsAFile(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        FileExtensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private async void SavePage(BrowserTab tab)
    {
        if (ShowsAFile(tab.Url) || !IsWebUrl(tab.Url))
        {
            tab.Download(tab.Url, askWhere: true);
            return;
        }

        if (XamlRoot is null) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = tab.SuggestedPageFileName(),
        };
        picker.FileTypeChoices.Add(L.T("Web page, single file", "Página web, un solo archivo"), [".mhtml"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await tab.SavePageAsMhtmlAsync(file.Path);
    }
}
