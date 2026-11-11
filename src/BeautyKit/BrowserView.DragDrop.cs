using CefSharp;
using CefSharp.Enums;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BeautyKit;

// Drops from other apps (Explorer files, links, text). Drags that start inside the page go through
// BrowserTab.StartDragging.
public sealed partial class BrowserView
{
    private bool _externalDrag;
    private readonly LinkDragVisual _linkVisual = new();

    private void SetupDragAndDrop()
    {
        AllowDrop = true;

        DragEnter += async (_, e) =>
        {
            if (_tab is not { } tab) return;
            var deferral = e.GetDeferral();
            try
            {
                var data = await CreateDragDataAsync(e.DataView);
                if (data is null)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                var point = e.GetPosition(this);
                tab.ExternalDragEnter(data, (int)point.X, (int)point.Y);
                await _linkVisual.PrepareAsync(e.DataView, _layers, tab.Favicon,
                    Uri.TryCreate(tab.Url, UriKind.Absolute, out var page) ? page.Host : null);
                _externalDrag = true;
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            finally
            {
                deferral.Complete();
            }
        };

        DragOver += (_, e) =>
        {
            if (!_externalDrag || _tab is null) return;
            var point = e.GetPosition(this);
            var operation = _tab.ExternalDragOver((int)point.X, (int)point.Y);
            e.AcceptedOperation = ToDataPackageOperation(operation);
            _linkVisual.Apply(e.DragUIOverride);
            e.Handled = true;
        };

        DragLeave += (_, _) =>
        {
            if (!_externalDrag) return;
            _externalDrag = false;
            _tab?.ExternalDragLeave();
        };

        Drop += (_, e) =>
        {
            if (!_externalDrag || _tab is null) return;
            _externalDrag = false;
            var point = e.GetPosition(this);
            _tab.ExternalDrop((int)point.X, (int)point.Y);
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        };
    }

    private static async Task<IDragData?> CreateDragDataAsync(DataPackageView view)
    {
        var data = CefSharp.Core.ObjectFactory.CreateDragData();

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await view.GetStorageItemsAsync()).OfType<StorageFile>().ToList();
            if (files.Count == 0) return null;
            foreach (var file in files) data.AddFile(file.Path, file.Name);
            data.IsFile = true;
            return data;
        }

        if (view.Contains(StandardDataFormats.WebLink))
        {
            var link = await view.GetWebLinkAsync();
            data.LinkUrl = link.ToString();
            data.IsLink = true;
            return data;
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            data.FragmentText = await view.GetTextAsync();
            data.IsFragment = true;
            return data;
        }

        return null;
    }

    // CEF answers None until the page has handled dragover; show Copy and let the page decide on drop.
    private static DataPackageOperation ToDataPackageOperation(DragOperationsMask operation) =>
        operation.HasFlag(DragOperationsMask.Move) ? DataPackageOperation.Move
        : operation.HasFlag(DragOperationsMask.Link) && !operation.HasFlag(DragOperationsMask.Copy) ? DataPackageOperation.Link
        : DataPackageOperation.Copy;
}
