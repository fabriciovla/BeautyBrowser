using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace BeautyKit;

/// <summary>
/// What a dragged link looks like while it is over the app: the site's icon and the address, instead of Windows'
/// generic globe (Chromium hands Windows no picture of its own). Drop targets show it through DragUIOverride.
/// </summary>
public sealed class LinkDragVisual
{
    private SoftwareBitmap? _bitmap;
    private string? _url;

    /// <summary>Prepares the picture for the link being dragged in, if it is one. Call it on DragEnter.</summary>
    /// <param name="host">A panel in the window: the card is drawn there for a moment, under everything else.</param>
    /// <param name="favicon">The icon to use when known (the page's own); otherwise the site's is fetched.</param>
    public async Task PrepareAsync(DataPackageView data, Panel host, ImageSource? favicon = null, string? faviconHost = null)
    {
        if (!data.Contains(StandardDataFormats.WebLink)) { _bitmap = null; return; }
        var link = await data.GetWebLinkAsync();
        if (link.AbsoluteUri == _url && _bitmap is not null) return;
        _url = link.AbsoluteUri;
        _bitmap = null;
        try
        {
            _bitmap = await RenderAsync(link, host, faviconHost == link.Host ? favicon : null);
        }
        catch { /* Windows' own picture stays */ }
    }

    /// <summary>Shows the card on DragOver; nothing changes when it is not a link.</summary>
    public void Apply(DragUIOverride ui)
    {
        if (_bitmap is not null) ui.SetContentFromSoftwareBitmap(_bitmap, new Windows.Foundation.Point(-12, -12));
    }

    public void Reset()
    {
        _bitmap = null;
        _url = null;
    }

    private static async Task<SoftwareBitmap> RenderAsync(Uri link, Panel host, ImageSource? icon)
    {
        if (icon is null && link.Scheme is "http" or "https")
        {
            var load = Favicons.LoadAsync($"{link.Scheme}://{link.Authority}/favicon.ico");
            if (await Task.WhenAny(load, Task.Delay(400)) == load) icon = await load;
        }

        var host_ = HostName.WithoutWww(link.Host);
        var address = new TextBlock
        {
            Text = host_ + link.PathAndQuery.TrimEnd('/'),
            FontSize = 12,
            MaxWidth = 300,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 40, 40, 40)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(icon is not null
            ? new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }); // link
        row.Children.Add(address);

        var card = new Border
        {
            Child = row,
            Padding = new Thickness(10, 7, 12, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // RenderTargetBitmap only draws elements in the live tree; underneath everything it is never seen.
        host.Children.Insert(0, card);
        try
        {
            card.UpdateLayout();
            var render = new RenderTargetBitmap();
            await render.RenderAsync(card);
            var pixels = await render.GetPixelsAsync();
            return SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8,
                render.PixelWidth, render.PixelHeight, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            host.Children.Remove(card);
        }
    }
}
