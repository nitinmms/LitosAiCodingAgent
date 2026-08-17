using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;
using ColorName16 = Terminal.Gui.Drawing.ColorName16;
using TextStyle = Terminal.Gui.Drawing.TextStyle;
using VisualRole = Terminal.Gui.Drawing.VisualRole;

namespace Litos.Console.Terminal;

/// <summary>
/// Colored unified-diff display, replacing Rendering/DiffRenderer.cs's Spectre.Console.Panel
/// output. A plain View that draws each diff line with its own Terminal.Gui Attribute instead of
/// producing a markup string — "+++"/"---" bold, "+" green, "-" red, everything else the default
/// foreground. Sized to its content via Dim.Auto in the constructor.
/// </summary>
public sealed class DiffView : View
{
    private string[] _lines;

    public DiffView(string unifiedDiff)
    {
        _lines = Split(unifiedDiff);

        Width = Dim.Auto();
        Height = Dim.Absolute(Math.Max(1, _lines.Length));
        CanFocus = false;
    }

    /// <summary>
    /// Replaces the displayed diff in place — used by /reflect's live, re-rendered-on-keystroke
    /// diff (ReflectDialog), so a new DiffView doesn't need to be recreated and re-added to its
    /// parent on every keystroke. Height grows/shrinks with the new content, same as the
    /// constructor's initial sizing.
    /// </summary>
    public void SetDiff(string unifiedDiff)
    {
        _lines = Split(unifiedDiff);
        Height = Dim.Absolute(Math.Max(1, _lines.Length));
        SetNeedsDraw();
    }

    private static string[] Split(string unifiedDiff) =>
        unifiedDiff.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    protected override bool OnDrawingText()
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        var bold = new Attribute(normal.Foreground, normal.Background, TextStyle.Bold);
        var green = new Attribute(new Color(ColorName16.Green), normal.Background);
        var red = new Attribute(new Color(ColorName16.Red), normal.Background);

        for (var row = 0; row < _lines.Length; row++)
        {
            var line = _lines[row];
            var attribute = line switch
            {
                _ when line.StartsWith("+++") || line.StartsWith("---") => bold,
                _ when line.StartsWith('+') => green,
                _ when line.StartsWith('-') => red,
                _ => normal,
            };

            Move(0, row);
            SetAttribute(attribute);
            AddStr(line);
        }

        return true;
    }
}
