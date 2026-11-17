namespace Litos.Probe.Web.Browser;

/// <summary>JavaScript evaluated in the page under test. Each one only reads the DOM.</summary>
internal static class PageScripts
{
    /// <summary>
    /// Gate step 8, the runtime selector lint (testability-architecture.md §3): every visible
    /// interactive element must have an accessible name or a <c>data-testid</c>, or no test can
    /// find it by role, label or test id. The name follows the browser's computation closely
    /// enough for a lint: aria-labelledby, aria-label, a label, a button's value, the text,
    /// an image's alt, an SVG title, title, then placeholder.
    /// </summary>
    public const string Lint = """
        () => {
          const interactive = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[contenteditable=""],[contenteditable=true],'
            + '[role=button],[role=link],[role=checkbox],[role=radio],[role=switch],[role=tab],[role=menuitem],[role=combobox],[role=textbox],[role=option],'
            + '[tabindex]:not([tabindex="-1"])';
          const visible = el => {
            const r = el.getBoundingClientRect(), s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
          };
          const text = el => (el.innerText || el.textContent || '').trim();
          const name = el => {
            const ids = el.getAttribute('aria-labelledby');
            if (ids) {
              const n = ids.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(text).join(' ').trim();
              if (n) return n;
            }
            const aria = (el.getAttribute('aria-label') || '').trim();
            if (aria) return aria;
            if (el.labels && el.labels.length) {
              const n = [...el.labels].map(text).join(' ').trim();
              if (n) return n;
            }
            if (el.tagName === 'INPUT' && ['submit', 'button', 'reset'].includes(el.type))
              return el.value || (el.type === 'submit' ? 'Submit' : el.type === 'reset' ? 'Reset' : '');
            if (el.tagName === 'INPUT' && el.type === 'image') return (el.alt || '').trim();
            if (!['INPUT', 'SELECT', 'TEXTAREA'].includes(el.tagName)) {
              const t = text(el);
              if (t) return t;
              const img = el.querySelector('img[alt]');
              if (img && img.alt.trim()) return img.alt.trim();
              const title = el.querySelector('svg title');
              if (title && title.textContent.trim()) return title.textContent.trim();
            }
            if ((el.title || '').trim()) return el.title.trim();
            if ((el.getAttribute('placeholder') || '').trim()) return el.getAttribute('placeholder').trim();
            return '';
          };
          const describe = el => {
            let d = el.tagName.toLowerCase();
            if (el.id) d += '#' + el.id;
            for (const a of ['type', 'name', 'role', 'href']) if (el.getAttribute(a)) d += `[${a}=${el.getAttribute(a)}]`;
            const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2) : [];
            if (cls.length) d += '.' + cls.join('.');
            return d.length > 160 ? d.slice(0, 160) + '…' : d;
          };
          const elements = [...document.querySelectorAll(interactive)].filter(visible);
          const unnamed = elements.filter(el => !el.hasAttribute('data-testid') && !name(el));
          return { interactive: elements.length, unnamed: unnamed.map(describe) };
        }
        """;

    /// <summary>For the dry run: does the page offer a way to sign in?</summary>
    public const string HasLogin = """
        () => !!document.querySelector('input[type=password]')
          || [...document.querySelectorAll('a,button')].some(el => /\b(log ?in|sign ?in)\b/i.test(el.innerText || ''))
        """;
}

internal sealed record LintResult(int Interactive, string[] Unnamed);
