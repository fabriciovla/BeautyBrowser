namespace BeautyKit;

public sealed partial class BrowserTab
{
    /// <summary>
    /// The last time the user was on this tab (UTC): when it was created, selected, or left. Tabs not used for long
    /// are archived (see BrowserShell).
    /// </summary>
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
}
