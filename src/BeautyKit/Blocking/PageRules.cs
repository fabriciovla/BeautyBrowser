using System.Text.Json.Serialization;

namespace BeautyKit.Blocking;

/// <summary>
/// What each page's render process needs before the site's own scripts run: cosmetic filters and scriptlets.
/// The browser writes it when it compiles the lists; every render process reads it (see <see cref="PageInjector"/>).
/// </summary>
public sealed class PageRules
{
    public List<string> Generic { get; set; } = [];

    /// <summary>Selectors per site: a host, a domain or an entity such as "google.*".</summary>
    public Dictionary<string, List<string>> Specific { get; set; } = [];

    /// <summary>Selectors a site must not hide (#@#).</summary>
    public Dictionary<string, List<string>> Exceptions { get; set; } = [];

    /// <summary>uBlock procedural filters (:has-text, :upward, :remove…) per site; "" applies everywhere.</summary>
    public Dictionary<string, List<string>> Procedural { get; set; } = [];

    /// <summary>selector:style(...) filters per site, already turned into CSS rules.</summary>
    public Dictionary<string, List<string>> Styles { get; set; } = [];

    /// <summary>Scriptlets per site as JSON [name, args…]; a leading "-" marks an exception.</summary>
    public Dictionary<string, List<string>> Scriptlets { get; set; } = [];

    /// <summary>Scriptlets only trusted lists (uBlock's own) may use.</summary>
    public HashSet<string> TrustedScriptlets { get; set; } = [];

    public HashSet<string> NoGeneric { get; set; } = [];
    public HashSet<string> NoCosmetic { get; set; } = [];
    public HashSet<string> NoSpecific { get; set; } = [];
    public HashSet<string> NoScriptlets { get; set; } = [];

    /// <summary>uBlock scriptlet code (function and dependencies) by name.</summary>
    public Dictionary<string, ScriptletResource> Resources { get; set; } = [];

    /// <summary>Scriptlet aliases ("nostif.js" → "no-setTimeout-if.js").</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];
}

public sealed class ScriptletResource
{
    /// <summary>Name of the function in <see cref="Code"/>, used to call it.</summary>
    public string Function { get; set; } = "";
    public string Code { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
    public bool RequiresTrust { get; set; }
}

/// <summary>Blocker options every render process has to honor.</summary>
public sealed class BlockerState
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowedSites { get; set; } = [];
}

/// <summary>What the blocker has blocked since it started counting, kept between sessions.</summary>
public sealed class BlockerStats
{
    public long Ads { get; set; }
    public long Trackers { get; set; }
    public long Bytes { get; set; }
    public long Milliseconds { get; set; }
    public DateTime Since { get; set; } = DateTime.UtcNow;
}

[JsonSerializable(typeof(PageRules))]
[JsonSerializable(typeof(BlockerState))]
[JsonSerializable(typeof(BlockerStats))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class BlockingJson : JsonSerializerContext;
