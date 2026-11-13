using System.Text.Json;
using CefSharp;

namespace BeautyKit;

// Boosts on the open page: applying a change right away, and "Hide elements" (hover outlines an element, a click
// hides it and reports its selector; Esc ends). See Boosts.
public sealed partial class BrowserTab
{
    /// <summary>"Hide elements" picked one: its CSS selector, to save in the site's Boost.</summary>
    public event EventHandler<string>? BoostElementPicked;

    /// <summary>"Hide elements" ended on the page (Esc).</summary>
    public event EventHandler? BoostPickerEnded;

    /// <summary>Puts the Boost on the page now (the next loads get it from the render process).</summary>
    public void ApplyBoost(SiteBoost boost)
    {
        if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return;
        frame.ExecuteJavaScriptAsync(Boosts.StyleScript(Boosts.CssFor(boost)));
    }

    public void StartBoostPicker()
    {
        if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return;
        frame.ExecuteJavaScriptAsync(PickerScript.Replace("__TOKEN__", JsonSerializer.Serialize(BrowserEngine.PageToken)));
    }

    public void StopBoostPicker()
    {
        if (!_browser.IsBrowserInitialized || _browser.GetMainFrame() is not { IsValid: true } frame) return;
        frame.ExecuteJavaScriptAsync("window.__bbZap&&window.__bbZap();");
    }

    /// <summary>A "bb-boost" message from the picker (main frame and the run's token only, like the passwords).</summary>
    private void ReceiveBoostMessage(IDictionary<string, object?> message)
    {
        if (message.TryGetValue("done", out var done) && done is true)
        {
            OnUi(() => BoostPickerEnded?.Invoke(this, EventArgs.Empty));
            return;
        }
        if (message.TryGetValue("selector", out var value) && value is string selector && Boosts.IsSafeSelector(selector))
            OnUi(() => BoostElementPicked?.Invoke(this, selector));
    }

    // The element under the mouse gets a blue outline; a click hides it (it never reaches the page) and reports a
    // selector: its id when it has a stable one, otherwise a short path of tags, classes and positions.
    private const string PickerScript = """
        (function (token) {
          if (window.__bbZap) return;
          const post = (m) => { try { const c = window.CefSharp || window.cefSharp; if (c && c.PostMessage) c.PostMessage(Object.assign({ type: 'bb-boost', token: token }, m)); } catch (e) {} };
          const ok = (s) => /^[A-Za-z][\w-]*$/.test(s);
          const box = document.createElement('div');
          box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;border:2px solid #3b82f6;background:rgba(59,130,246,.14);border-radius:5px;transition:all .06s ease-out;display:none';
          document.documentElement.appendChild(box);
          let current = null;
          const selector = (el) => {
            const parts = [];
            while (el && el.nodeType === 1 && el !== document.body && el !== document.documentElement && parts.length < 6) {
              if (el.id && ok(el.id)) { parts.unshift('#' + el.id); break; }
              let part = el.tagName.toLowerCase();
              const classes = Array.from(el.classList).filter(ok).slice(0, 2);
              if (classes.length) part += '.' + classes.join('.');
              const parent = el.parentElement;
              if (parent) {
                const same = Array.from(parent.children).filter((x) => x.tagName === el.tagName);
                if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(el) + 1) + ')';
              }
              parts.unshift(part);
              el = parent;
            }
            return parts.join(' > ');
          };
          const move = (e) => {
            const el = e.target;
            if (!el || el === box || el === document.documentElement || el === document.body) { box.style.display = 'none'; current = null; return; }
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { display: 'block', left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
          };
          const swallow = (e) => { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };
          const click = (e) => {
            swallow(e);
            if (!current) return;
            const s = selector(current);
            current.style.setProperty('display', 'none', 'important');
            box.style.display = 'none';
            current = null;
            if (s) post({ selector: s });
          };
          const key = (e) => { if (e.key === 'Escape') { swallow(e); stop(); post({ done: true }); } };
          const stop = () => {
            document.removeEventListener('mousemove', move, true);
            document.removeEventListener('click', click, true);
            document.removeEventListener('mousedown', swallow, true);
            document.removeEventListener('mouseup', swallow, true);
            document.removeEventListener('keydown', key, true);
            box.remove();
            window.__bbZap = null;
          };
          document.addEventListener('mousemove', move, true);
          document.addEventListener('click', click, true);
          document.addEventListener('mousedown', swallow, true);
          document.addEventListener('mouseup', swallow, true);
          document.addEventListener('keydown', key, true);
          window.__bbZap = stop;
        })(__TOKEN__);
        """;
}
