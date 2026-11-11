namespace BeautyKit.Blocking;

/// <summary>JavaScript injected into pages (see <see cref="PageInjector"/>).</summary>
internal static class PageScripts
{
    /// <summary>
    /// Element hiding: the CSS goes into an adopted stylesheet (not a &lt;style&gt; the page can touch) and uBlock's
    /// procedural filters (:has-text, :upward, :remove…) run on load and whenever the page changes.
    /// Called as (function)(css, procedural).
    /// </summary>
    public const string Cosmetic = """
        function (css, procedural) {
          'use strict';
          if (css) {
            let sheet = null;
            try { sheet = new CSSStyleSheet(); sheet.replaceSync(css); document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; }
            catch (e) { sheet = null; }
            const ensure = () => {
              if (sheet) {
                if (!document.adoptedStyleSheets.includes(sheet)) document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
              } else if (!document.getElementById('__bb_cosmetic')) {
                const style = document.createElement('style');
                style.id = '__bb_cosmetic';
                style.textContent = css;
                (document.head || document.documentElement).appendChild(style);
              }
            };
            if (!sheet && document.documentElement) ensure();
            document.addEventListener('DOMContentLoaded', ensure, { once: true });
            addEventListener('load', ensure, { once: true });
          }
          if (!procedural || procedural.length === 0) return;

          const OPS = ['has-text', '-abp-contains', 'contains', 'has', '-abp-has', 'if', 'not', 'if-not', 'upward',
            'nth-ancestor', 'matches-css', 'matches-css-before', 'matches-css-after', 'min-text-length', 'matches-attr',
            'matches-path', 'matches-media', 'xpath', 'watch-attr', 'remove', 'style', 'remove-attr', 'remove-class'];
          const toRegex = (s) => {
            s = s.trim();
            if (/^(['"]).*\1$/.test(s)) s = s.slice(1, -1);
            const m = /^\/(.+)\/([imsu]*)$/.exec(s);
            if (m) { try { return new RegExp(m[1], m[2]); } catch (e) { return null; } }
            return new RegExp(s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
          };
          // Finds a top-level ":op(" (outside parentheses, brackets and quotes).
          const findOp = (s, from) => {
            let depth = 0, bracket = 0, quote = '';
            for (let i = from; i < s.length; i++) {
              const c = s[i];
              if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
              if (c === '"' || c === "'") { quote = c; continue; }
              if (c === '[') bracket++; else if (c === ']') bracket--;
              else if (c === '(') depth++; else if (c === ')') depth--;
              else if (c === ':' && depth === 0 && bracket === 0) {
                for (const op of OPS) {
                  if (s.startsWith(op + '(', i + 1)) {
                    const procedural = !['has', 'not', 'if', 'if-not'].includes(op) || isProcedural(argAt(s, i + 1 + op.length).arg);
                    if (procedural || op === 'if' || op === 'if-not') return { at: i, op };
                  }
                }
              }
            }
            return null;
          };
          const argAt = (s, open) => {
            let depth = 0, quote = '';
            for (let i = open; i < s.length; i++) {
              const c = s[i];
              if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
              if (c === '"' || c === "'") { quote = c; continue; }
              if (c === '(') depth++;
              else if (c === ')') { depth--; if (depth === 0) return { arg: s.slice(open + 1, i), end: i + 1 }; }
            }
            return { arg: s.slice(open + 1), end: s.length };
          };
          const isProcedural = (s) => OPS.some(op => !['has', 'not'].includes(op) && s.includes(':' + op + '('));
          const compile = (s) => {
            s = s.trim();
            const first = findOp(s, 0);
            if (!first) return { css: s, tasks: [] };
            const out = { css: s.slice(0, first.at).trim(), tasks: [], action: null };
            let pos = first.at;
            while (pos < s.length) {
              const hit = findOp(s, pos);
              if (!hit || hit.at !== pos) {
                // Plain CSS between operators ("div:has-text(x) > span")
                const next = hit ? hit.at : s.length;
                const rest = s.slice(pos, next).trim();
                if (rest) out.tasks.push(['spath', rest]);
                pos = next;
                continue;
              }
              const { arg, end } = argAt(s, hit.at + 1 + hit.op.length);
              pos = end;
              switch (hit.op) {
                case 'remove': case 'style': case 'remove-attr': case 'remove-class':
                  out.action = [hit.op, arg]; break;
                case 'has-text': case '-abp-contains': case 'contains': {
                  const re = toRegex(arg); if (!re) return null; out.tasks.push(['text', re]); break; }
                case 'has': case '-abp-has': case 'if': {
                  const sub = isProcedural(arg) ? compile(arg) : null;
                  out.tasks.push(['has', sub || arg]); break; }
                case 'not': case 'if-not': {
                  const sub = isProcedural(arg) ? compile(arg) : null;
                  out.tasks.push(['not', sub || arg]); break; }
                case 'upward': case 'nth-ancestor': out.tasks.push(['up', /^\d+$/.test(arg.trim()) ? +arg : arg]); break;
                case 'matches-css': case 'matches-css-before': case 'matches-css-after': {
                  const colon = arg.indexOf(':'); if (colon < 0) return null;
                  const pseudo = hit.op.endsWith('before') ? '::before' : hit.op.endsWith('after') ? '::after' : null;
                  const re = toRegex(arg.slice(colon + 1)); if (!re) return null;
                  out.tasks.push(['css', arg.slice(0, colon).trim(), re, pseudo]); break; }
                case 'min-text-length': out.tasks.push(['minlen', +arg]); break;
                case 'matches-attr': {
                  const eq = arg.indexOf('=');
                  const name = toRegex(eq < 0 ? arg : arg.slice(0, eq)), value = eq < 0 ? null : toRegex(arg.slice(eq + 1));
                  if (!name) return null; out.tasks.push(['attr', name, value]); break; }
                case 'matches-path': { const re = toRegex(arg); if (!re) return null; out.tasks.push(['path', re]); break; }
                case 'matches-media': out.tasks.push(['media', arg]); break;
                case 'xpath': out.tasks.push(['xpath', arg]); break;
                case 'watch-attr': break;
              }
            }
            return out;
          };
          const qsa = (root, sel) => {
            try { return root === document ? [...document.querySelectorAll(sel)] : [...root.querySelectorAll(':scope ' + sel)]; }
            catch (e) { return []; }
          };
          const run = (f, roots) => {
            let nodes = roots ? roots : (f.css ? qsa(document, f.css) : [document.documentElement]);
            if (roots && f.css) nodes = nodes.flatMap(n => /^[>+~]/.test(f.css) ? siblingsOrChildren(n, f.css) : qsa(n, f.css));
            for (const t of f.tasks) {
              if (nodes.length === 0) break;
              switch (t[0]) {
                case 'spath': nodes = nodes.flatMap(n => /^[>+~]/.test(t[1]) ? siblingsOrChildren(n, t[1]) : qsa(n, t[1])); break;
                case 'text': nodes = nodes.filter(n => t[1].test(n.textContent || '')); break;
                case 'has': nodes = nodes.filter(n => typeof t[1] === 'string'
                  ? matchesSafe(n, ':has(' + t[1] + ')') : run(t[1], [n]).length > 0); break;
                case 'not': nodes = nodes.filter(n => typeof t[1] === 'string'
                  ? !matchesSafe(n, t[1]) : run(t[1], [n]).length === 0); break;
                case 'up': nodes = nodes.map(n => {
                  if (typeof t[1] === 'number') { let p = n; for (let i = 0; i < t[1] && p; i++) p = p.parentElement; return p; }
                  try { return n.parentElement && n.parentElement.closest(t[1]); } catch (e) { return null; }
                }).filter(Boolean); break;
                case 'css': nodes = nodes.filter(n => t[2].test(getComputedStyle(n, t[3])[t[1]] || '')); break;
                case 'minlen': nodes = nodes.filter(n => (n.textContent || '').length >= t[1]); break;
                case 'attr': nodes = nodes.filter(n => [...n.attributes].some(a => t[1].test(a.name) && (!t[2] || t[2].test(a.value)))); break;
                case 'path': if (!t[1].test(location.pathname + location.search)) nodes = []; break;
                case 'media': try { if (!matchMedia(t[1]).matches) nodes = []; } catch (e) { nodes = []; } break;
                case 'xpath': nodes = nodes.flatMap(n => {
                  const out = []; try {
                    const r = document.evaluate(t[1], n, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                    for (let i = 0; i < r.snapshotLength; i++) { const x = r.snapshotItem(i); if (x.nodeType === 1) out.push(x); }
                  } catch (e) {} return out; }); break;
              }
            }
            return [...new Set(nodes)];
          };
          const matchesSafe = (n, sel) => { try { return n.matches(sel); } catch (e) { return false; } };
          const siblingsOrChildren = (n, sel) => {
            const op = sel[0], rest = sel.slice(1).trim();
            if (op === '>') return [...n.children].filter(c => matchesSafe(c, rest));
            const out = []; let s = n.nextElementSibling;
            while (s) { if (matchesSafe(s, rest)) out.push(s); if (op === '+') break; s = s.nextElementSibling; }
            return out;
          };
          const filters = procedural.map(compile).filter(Boolean);
          if (filters.length === 0) return;
          const done = new WeakSet();
          const apply = () => {
            for (const f of filters) {
              for (const n of run(f)) {
                if (!f.action) {
                  if (!done.has(n)) { done.add(n); n.style.setProperty('display', 'none', 'important'); }
                  continue;
                }
                const [what, arg] = f.action;
                if (what === 'remove') n.remove();
                else if (what === 'style') { n.setAttribute('style', (n.getAttribute('style') || '') + ';' + arg); }
                else if (what === 'remove-attr') { for (const a of arg.split('|')) n.removeAttribute(a.trim()); }
                else if (what === 'remove-class') { for (const c of arg.split('|')) n.classList.remove(c.trim()); }
              }
            }
          };
          let pending = 0;
          const schedule = () => { if (!pending) pending = setTimeout(() => { pending = 0; apply(); }, 120); };
          const start = () => {
            apply();
            new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true, attributes: true, characterData: true });
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
          else start();
        }
        """;

    /// <summary>
    /// Detects audio and video playing in the document, with its MediaSession metadata (title, artist, artwork), and
    /// reports it to the browser. It also exposes the commands the media card uses (play/pause, next, previous, seek).
    /// </summary>
    public const string Media = """
        (() => {
          'use strict';
          if (window.__bbMediaInstalled) return;
          Object.defineProperty(window, '__bbMediaInstalled', { value: true });
          // This document's id, so commands reach it and not another iframe.
          const token = Math.random().toString(36).slice(2) + Date.now().toString(36);
          const post = (m) => { try { const c = window.CefSharp || window.cefSharp; if (c && c.PostMessage) c.PostMessage(m); } catch (e) {} };
          const handlers = Object.create(null);
          const session = navigator.mediaSession;
          if (session && typeof session.setActionHandler === 'function') {
            const original = session.setActionHandler;
            session.setActionHandler = function (action, handler) {
              handlers[action] = handler;
              return original.call(this, action, handler);
            };
          }
          const known = new Set();
          let current = null;
          const EVENTS = ['play', 'playing', 'pause', 'ended', 'emptied', 'loadedmetadata', 'volumechange', 'durationchange', 'seeked', 'ratechange'];
          const onEvent = (e) => {
            const el = e.target;
            if (!(el instanceof HTMLMediaElement)) return;
            track(el);
            if (e.type === 'play' || e.type === 'playing') current = el;
            schedule();
          };
          const track = (el) => {
            if (known.has(el)) return;
            known.add(el);
            if (!el.isConnected) for (const t of EVENTS) el.addEventListener(t, onEvent, true);
          };
          for (const t of EVENTS) document.addEventListener(t, onEvent, true);
          const play = HTMLMediaElement.prototype.play;
          HTMLMediaElement.prototype.play = function () {
            track(this);
            current = this;
            return play.apply(this, arguments);
          };
          const pickArtwork = (md) => {
            if (!md || !md.artwork || !md.artwork.length) return '';
            let best = md.artwork[0], size = 0;
            for (const a of md.artwork) {
              const m = /(\d+)x(\d+)/.exec(a.sizes || '');
              const s = m ? +m[1] * +m[2] : 1;
              if (s > size) { size = s; best = a; }
            }
            try { return new URL(best.src, location.href).href; } catch (e) { return ''; }
          };
          const element = () => {
            if (current && (current.isConnected || !current.paused)) return current;
            for (const el of known) if (!el.paused) return (current = el);
            for (const el of document.querySelectorAll('video,audio')) if (!el.paused) return (current = el);
            return current && current.isConnected ? current : null;
          };
          const state = () => {
            const el = element();
            if (!el) return { type: 'bb-media', token, gone: true };
            const md = session && session.metadata;
            const d = el.duration;
            return {
              type: 'bb-media',
              token,
              playing: !el.paused && !el.ended,
              muted: el.muted || el.volume === 0,
              hasAudio: typeof el.webkitAudioDecodedByteCount !== 'number' || el.webkitAudioDecodedByteCount > 0 || el.currentTime < 1,
              video: el instanceof HTMLVideoElement,
              title: (md && md.title) || '',
              artist: (md && md.artist) || '',
              album: (md && md.album) || '',
              artwork: pickArtwork(md),
              position: el.currentTime || 0,
              duration: Number.isFinite(d) ? d : (d === Infinity ? -1 : 0),
              hasNext: typeof handlers.nexttrack === 'function',
              hasPrevious: typeof handlers.previoustrack === 'function',
            };
          };
          let timer = 0;
          const schedule = () => { if (!timer) timer = setTimeout(() => { timer = 0; post(state()); }, 150); };
          setInterval(() => { const el = element(); if (el && !el.paused) post(state()); }, 1000);
          Object.defineProperty(window, '__bbMedia', {
            value: (command, value, target) => {
              if (target !== token) return;
              const el = element();
              const call = (action, extra) => {
                const h = handlers[action];
                if (typeof h !== 'function') return false;
                try { h(Object.assign({ action }, extra || {})); return true; } catch (e) { return false; }
              };
              switch (command) {
                case 'toggle': if (el) { if (el.paused) el.play().catch(() => call('play')); else el.pause(); } break;
                case 'pause': if (el) el.pause(); break;
                case 'next': call('nexttrack'); break;
                case 'previous': if (!call('previoustrack') && el) el.currentTime = 0; break;
                case 'seek': if (!call('seekto', { seekTime: value, fastSeek: false }) && el) el.currentTime = value; break;
              }
              schedule();
            },
          });
        })();
        """;

    /// <summary>
    /// The password manager's eyes in the page (top document only): reports which login field has focus and where, so
    /// the app can offer saved accounts, and the user name and password when a login is sent, so it can offer to save
    /// them. window.__bbPwFill fills the focused form when the user picks an account.
    /// </summary>
    public const string Passwords = """
        (() => {
          'use strict';
          if (window.top !== window || window.__bbPwInstalled) return;
          Object.defineProperty(window, '__bbPwInstalled', { value: true });
          const post = (m) => { try { const c = window.CefSharp || window.cefSharp; if (c && c.PostMessage) c.PostMessage(Object.assign({ type: 'bb-pw', token: '__BB_PAGE_TOKEN__' }, m)); } catch (e) {} };
          const isInput = (el) => el instanceof HTMLInputElement;
          const visible = (el) => el.isConnected && !el.disabled && el.getClientRects().length > 0;
          const isPassword = (el) => isInput(el) && el.type === 'password';
          const isText = (el) => isInput(el) && /^(text|email|tel)$/.test(el.type);
          const isUser = (el) => isText(el) && (el.type === 'email' || /username|email/i.test(el.autocomplete || '')
            || /user|login|mail|account|ident|usuario|correo/i.test((el.name || '') + ' ' + (el.id || '')));
          const scope = (el) => el.form || (el.closest && el.closest('form')) || document;
          const passwords = (root) => [...root.querySelectorAll('input[type=password]')].filter(visible);
          // The user name of a password field: the last text field before it in its form (or a hidden one marked username).
          const userFor = (pw) => {
            let found = null;
            for (const i of scope(pw).querySelectorAll('input')) {
              if (i === pw) break;
              if (isText(i) && (visible(i) || /username/i.test(i.autocomplete || ''))) found = i;
            }
            return found;
          };
          // Sign-ins in two steps (the user name first, the password on the next page) keep the name for the session.
          let lastUser = '';
          try { lastUser = sessionStorage.getItem('__bbPwUser') || ''; } catch (e) {}
          const rememberUser = (v) => { if (!v) return; lastUser = v; try { sessionStorage.setItem('__bbPwUser', v); } catch (e) {} };

          let field = null;
          let typed = false;
          const report = (el) => {
            const r = el.getBoundingClientRect();
            const s = window.devicePixelRatio || 1;
            post({ kind: 'focus', x: r.left * s, y: r.bottom * s, width: r.width * s, password: isPassword(el) });
          };
          document.addEventListener('focusin', (e) => {
            const el = e.target;
            if (!(isPassword(el) || isUser(el))) return;
            field = el;
            typed = false;
            report(el);
          }, true);
          document.addEventListener('focusout', (e) => { if (e.target === field) post({ kind: 'blur' }); }, true);
          document.addEventListener('input', (e) => {
            if (isUser(e.target)) rememberUser(e.target.value);
            if (e.target === field && !typed && e.isTrusted) { typed = true; post({ kind: 'blur' }); }
          }, true);
          addEventListener('scroll', () => { if (field) post({ kind: 'blur' }); }, { capture: true, passive: true });

          let sent = '';
          const capture = (pw) => {
            if (!pw || !pw.value) return;
            const u = userFor(pw);
            const user = (u && u.value) || lastUser;
            if (u && u.value) rememberUser(u.value);
            const key = user + '\n' + pw.value;
            if (key === sent) return;
            sent = key;
            post({ kind: 'submit', user, password: pw.value });
          };
          document.addEventListener('submit', (e) => capture(passwords(e.target)[0]), true);
          document.addEventListener('keydown', (e) => { if (e.key === 'Enter' && isPassword(e.target)) capture(e.target); }, true);
          document.addEventListener('click', (e) => {
            const b = e.target.closest && e.target.closest('button, input[type=submit], [role=button]');
            if (!b || !e.isTrusted) return;
            capture(passwords(scope(b))[0] || passwords(document)[0]);
          }, true);

          Object.defineProperty(window, '__bbPwFill', { value: (user, password) => {
            const target = field && field.isConnected ? field : (passwords(document)[0] || null);
            if (!target) return;
            const pw = isPassword(target) ? target : passwords(scope(target))[0];
            const u = isUser(target) ? target : (pw ? userFor(pw) : null);
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            const set = (el, v) => {
              if (!el) return;
              setter.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            set(u, user);
            set(pw, password);
            rememberUser(user);
            post({ kind: 'blur' });
          } });
        })();
        """;
}
