using System.Text.Json;
using System.Text.Json.Serialization;
using CefSharp;
using CefSharp.OffScreen;

namespace BeautyKit.Blocking;

internal sealed class ExtractedScriptlet
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = [];
    [JsonPropertyName("fn")] public string Function { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("deps")] public List<string> Dependencies { get; set; } = [];
    [JsonPropertyName("trust")] public bool RequiresTrust { get; set; }
}

internal sealed class ExtractedRedirect
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alias")] public List<string> Aliases { get; set; } = [];
}

internal sealed class ExtractedResources
{
    [JsonPropertyName("scriptlets")] public List<ExtractedScriptlet> Scriptlets { get; set; } = [];
    [JsonPropertyName("redirects")] public List<ExtractedRedirect> Redirects { get; set; } = [];
}

[JsonSerializable(typeof(ExtractedResources))]
internal sealed partial class ExtractedJson : JsonSerializerContext;

/// <summary>
/// Extracts the code of every uBlock Origin scriptlet. Scriptlets are JavaScript modules that import each other and
/// uBlock builds the injected code with <c>fn.toString()</c>, so the same is done here: the modules are served from
/// disk to a hidden tab and the result is read back. Runs only when the lists are updated.
/// </summary>
internal static class ScriptletExtractor
{
    private const string Origin = "https://ubo.beautybrowser.invalid/";

    private const string Page = """
        <!doctype html><meta charset="utf-8"><title>extract</title>
        <script type="module">
        try {
          const { builtinScriptlets } = await import('./js/resources/scriptlets.js');
          const redirects = (await import('./js/redirect-resources.js')).default;
          const depName = d => typeof d === 'function' ? (d.details?.name ?? d.name) : d;
          const out = {
            scriptlets: builtinScriptlets.map(s => ({
              name: s.name, aliases: s.aliases || [], fn: s.fn.name, code: s.fn.toString(),
              deps: (s.dependencies || []).map(depName), trust: s.requiresTrust === true,
            })),
            redirects: [...redirects].map(([name, d]) => ({
              name, alias: d.alias ? [].concat(d.alias) : [],
            })),
          };
          window.__bbResult = JSON.stringify(out);
        } catch (e) {
          window.__bbResult = 'error:' + (e && e.stack || e);
        }
        </script>
        """;

    public static async Task<ExtractedResources?> RunAsync()
    {
        if (!File.Exists(Path.Combine(BlockerAssets.UboFolder, "js", "resources", "scriptlets.js"))) return null;

        var settings = CefSharp.Core.ObjectFactory.CreateBrowserSettings(autoDispose: true);
        using var browser = new ChromiumWebBrowser(Origin + "extract.html", settings, automaticallyCreateBrowser: false)
        {
            RequestHandler = new LocalFilesRequestHandler(),
        };
        var windowInfo = CefSharp.Core.ObjectFactory.CreateWindowInfo();
        windowInfo.SetAsWindowless(0);
        browser.CreateBrowser(windowInfo, settings);

        try
        {
            await browser.WaitForInitialLoadAsync().WaitAsync(TimeSpan.FromSeconds(20));
            for (var i = 0; i < 100; i++)
            {
                var response = await browser.EvaluateScriptAsync("window.__bbResult || ''");
                if (response.Success && response.Result is string { Length: > 0 } json)
                {
                    if (json.StartsWith("error:", StringComparison.Ordinal))
                    {
                        ContentBlocker.Log("Scriptlets: " + json);
                        return null;
                    }
                    var result = JsonSerializer.Deserialize(json, ExtractedJson.Default.ExtractedResources);
                    return result is { Scriptlets.Count: > 0 } ? result : null;
                }
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            ContentBlocker.Log("Scriptlets: " + ex.Message);
        }
        return null;
    }

    private sealed class LocalFilesRequestHandler : CefSharp.Handler.RequestHandler
    {
        protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
            IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
            string requestInitiator, ref bool disableDefaultHandling) => new Files();
    }

    private sealed class Files : CefSharp.Handler.ResourceRequestHandler
    {
        protected override IResourceHandler? GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request)
        {
            if (!request.Url.StartsWith(Origin, StringComparison.Ordinal)) return ResourceHandler.FromString("", mimeType: "text/plain");
            var path = Uri.UnescapeDataString(new Uri(request.Url).AbsolutePath.TrimStart('/'));
            if (path == "extract.html") return ResourceHandler.FromString(Page, mimeType: "text/html");

            var full = Path.GetFullPath(Path.Combine(BlockerAssets.UboFolder, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(BlockerAssets.UboFolder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return ResourceHandler.ForErrorMessage("not found", System.Net.HttpStatusCode.NotFound);
            return ResourceHandler.FromFilePath(full, "text/javascript");
        }
    }
}
