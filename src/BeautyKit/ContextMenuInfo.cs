using CefSharp;

namespace BeautyKit;

/// <summary>A copy of CEF's context menu parameters, which are only valid during the callback.</summary>
internal sealed record ContextMenuInfo(
    int X,
    int Y,
    string LinkUrl,
    string SourceUrl,
    ContextMenuMediaType MediaType,
    bool HasImageContents,
    string SelectionText,
    bool IsEditable,
    ContextMenuEditState EditState,
    string MisspelledWord,
    IReadOnlyList<string> Suggestions)
{
    public bool HasLink => !string.IsNullOrEmpty(LinkUrl);
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectionText);
    public bool IsImage => MediaType == ContextMenuMediaType.Image && !string.IsNullOrEmpty(SourceUrl);
    public bool IsVideo => MediaType == ContextMenuMediaType.Video && !string.IsNullOrEmpty(SourceUrl);
    public bool IsAudio => MediaType == ContextMenuMediaType.Audio && !string.IsNullOrEmpty(SourceUrl);
    public bool Can(ContextMenuEditState state) => EditState.HasFlag(state);

    // Read from the page at (X, Y) when the menu opens (CEF does not give the link text or the video state).
    public string LinkText { get; init; } = "";
    public bool VideoLoop { get; init; }
    public bool VideoControls { get; init; }
    public bool CanPictureInPicture { get; init; }
}
