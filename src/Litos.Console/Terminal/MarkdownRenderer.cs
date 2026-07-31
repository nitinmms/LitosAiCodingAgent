using System.Text;
using System.Text.RegularExpressions;

namespace Litos.Console.Terminal;

/// <summary>
/// Best-effort markdown text preparation for streamed model text. Under Spectre.Console this
/// type used to hand-roll bold/italic/heading/inline-code detection via regex and emit Spectre
/// markup strings (see git history) — Terminal.Gui v2 ships a real <c>Terminal.Gui.Views.Markdown</c>
/// view (backed by Markdig) that already understands CommonMark styling natively, so that regex
/// layer is no longer needed: <see cref="ToDisplayText"/> now only does the LaTeX-math rewriting
/// (which Markdig has no concept of) and hands back plain markdown text for <c>Markdown.Text</c>
/// to render with real styling.
///
/// Not a LaTeX parser: only the constructs models commonly emit are covered (\frac, \sqrt, common
/// Greek letters/symbols, delimiter stripping for $, $$, \(\), \[\]). ^/_ super/subscript only get
/// real Unicode glyphs for digits and +-=()n, since those are the only characters with dedicated
/// superscript/subscript codepoints; anything else (e.g. a multi-letter exponent) is left as plain
/// text rather than guessing.
/// </summary>
public static class MarkdownRenderer
{
    // \frac{a}{b} -> (a)/(b); nested/simple {..} content only, not a brace-matching parser.
    private static readonly Regex FracPattern = new(@"\\frac\{([^{}]*)\}\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SqrtPattern = new(@"\\sqrt\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SuperscriptPattern = new(@"\^\{?([0-9+\-=()n]+)\}?", RegexOptions.Compiled);
    private static readonly Regex SubscriptPattern = new(@"_\{?([0-9+\-=()n]+)\}?", RegexOptions.Compiled);
    // $$...$$ before $...$ so the double-dollar block delimiter isn't left as two singles.
    private static readonly Regex DisplayMathDelimiterPattern = new(@"\$\$(.+?)\$\$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex InlineMathDelimiterPattern = new(@"\$(.+?)\$", RegexOptions.Compiled);
    private static readonly Regex ParenMathDelimiterPattern = new(@"\\\((.+?)\\\)", RegexOptions.Compiled);
    private static readonly Regex BracketMathDelimiterPattern = new(@"\\\[(.+?)\\\]", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly IReadOnlyDictionary<string, string> GreekLetters = new Dictionary<string, string>
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ",
        ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["chi"] = "χ",
        ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
    };

    private static readonly IReadOnlyDictionary<string, string> MathSymbols = new Dictionary<string, string>
    {
        [@"\times"] = "×", [@"\cdot"] = "·", [@"\div"] = "÷", [@"\pm"] = "±", [@"\mp"] = "∓",
        [@"\leq"] = "≤", [@"\geq"] = "≥", [@"\neq"] = "≠", [@"\approx"] = "≈", [@"\equiv"] = "≡",
        [@"\infty"] = "∞", [@"\sum"] = "Σ", [@"\prod"] = "Π", [@"\int"] = "∫", [@"\partial"] = "∂",
        [@"\rightarrow"] = "→", [@"\to"] = "→", [@"\leftarrow"] = "←", [@"\Rightarrow"] = "⇒",
        [@"\in"] = "∈", [@"\notin"] = "∉", [@"\subset"] = "⊂", [@"\cup"] = "∪", [@"\cap"] = "∩",
        [@"\emptyset"] = "∅", [@"\forall"] = "∀", [@"\exists"] = "∃", [@"\sqrt"] = "√",
    };

    // Only digits, +, -, =, (, ), and n have Unicode super/subscript codepoints; anything
    // else (e.g. ^{2x}) is left with a plain ^/_ prefix since there's no glyph for it.
    private static readonly IReadOnlyDictionary<char, char> SuperscriptGlyphs = new Dictionary<char, char>
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵',
        ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹', ['+'] = '⁺', ['-'] = '⁻',
        ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ',
    };

    private static readonly IReadOnlyDictionary<char, char> SubscriptGlyphs = new Dictionary<char, char>
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅',
        ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉', ['+'] = '₊', ['-'] = '₋',
        ['='] = '₌', ['('] = '₍', [')'] = '₎', ['n'] = 'ₙ',
    };

    private static readonly Regex GreekLetterPattern = new(
        @"\\(" + string.Join('|', GreekLetters.Keys) + @")\b", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites LaTeX math into plain text/Unicode and returns the result as plain markdown,
    /// ready to hand to <c>Terminal.Gui.Views.Markdown.Text</c> (or any other CommonMark-aware
    /// renderer) for real bold/italic/heading/code styling — no markup-string escaping needed
    /// here since Terminal.Gui's Markdown view parses CommonMark itself.
    /// </summary>
    public static string ToDisplayText(string text) => RewriteLatexMath(text);

    private static string RewriteLatexMath(string text)
    {
        text = DisplayMathDelimiterPattern.Replace(text, m => RewriteMathBody(m.Groups[1].Value));
        text = InlineMathDelimiterPattern.Replace(text, m => RewriteMathBody(m.Groups[1].Value));
        text = ParenMathDelimiterPattern.Replace(text, m => RewriteMathBody(m.Groups[1].Value));
        text = BracketMathDelimiterPattern.Replace(text, m => RewriteMathBody(m.Groups[1].Value));
        return text;
    }

    private static string RewriteMathBody(string body)
    {
        // \frac{a}{b} -> (a)/(b) first, since its arguments may themselves contain \sqrt,
        // Greek letters, etc. that still need rewriting afterward.
        body = FracPattern.Replace(body, m => $"({RewriteMathBody(m.Groups[1].Value)})/({RewriteMathBody(m.Groups[2].Value)})");
        body = SqrtPattern.Replace(body, m => $"√({RewriteMathBody(m.Groups[1].Value)})");

        foreach (var (name, glyph) in GreekLetters)
            body = body.Replace(@"\" + name, glyph);
        foreach (var (macro, glyph) in MathSymbols)
            body = body.Replace(macro, glyph);

        // The capture groups only ever match characters present in the glyph tables above
        // (see the character classes on SuperscriptPattern/SubscriptPattern), so every
        // character here always has a mapping — anything outside that class (e.g. the "x"
        // in "x+1") is left outside the match, untouched as plain text.
        body = SuperscriptPattern.Replace(body, m => ToGlyphs(m.Groups[1].Value, SuperscriptGlyphs));
        body = SubscriptPattern.Replace(body, m => ToGlyphs(m.Groups[1].Value, SubscriptGlyphs));

        return body.Trim();
    }

    private static string ToGlyphs(string chars, IReadOnlyDictionary<char, char> glyphs)
    {
        var sb = new StringBuilder(chars.Length);
        foreach (var ch in chars)
            sb.Append(glyphs[ch]);
        return sb.ToString();
    }
}
