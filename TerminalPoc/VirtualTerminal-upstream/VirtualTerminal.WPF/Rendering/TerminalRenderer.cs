using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using VirtualTerminal.Buffer;
using VirtualTerminal.Enums;
using VirtualTerminal.Model;
using VirtualTerminal.Options;
using Color = VirtualTerminal.Model.TerminalColor;
using WindowsColor = System.Windows.Media.Color;

namespace VirtualTerminal.Rendering;

/// <summary>
/// Paints terminal rows using <see cref="GlyphRun"/>: coalesces same-style cells into runs,
/// resolves default/inverse/conceal colors, draws backgrounds, underlines, strikethrough,
/// overline, and the cursor. Positioning is column-based (monospace advance assumption).
/// </summary>
public sealed class TerminalRenderer : IDisposable
{
    private readonly GlyphCache _glyphs = new();
    private readonly Dictionary<uint, Brush> _brushes = [];
    private readonly List<char> _chars = [];
    private readonly List<ushort> _glyphIndices = [];
    private readonly List<double> _advanceWidths = [];
    private readonly List<ushort> _clusterMap = [];
    private TerminalOptions _options = new();
    private double _emSize = 14;

    /// <summary>Gets a value indicating whether the renderer has been configured with a valid font.</summary>
    public bool IsValid => _glyphs.IsValid;

    /// <summary>Gets the size of one terminal cell in device-independent pixels.</summary>
    public Size CellSize => new Size(_glyphs.CellWidth, _glyphs.CellHeight);

    /// <summary>Gets the baseline offset within a cell.</summary>
    public double Baseline => _glyphs.Ascent;

    /// <summary>Gets the width of one terminal cell.</summary>
    public double CellWidth => _glyphs.CellWidth;

    /// <summary>Gets the height of one terminal cell.</summary>
    public double CellHeight => _glyphs.CellHeight;

    /// <summary>Configures the renderer with the specified font family, size, and terminal options.</summary>
    public void Configure(string family, double emSize, TerminalOptions options)
    {
        _options = options;
        _emSize = emSize;

        _glyphs.Configure(family, emSize, options.LineHeight, options.FontFallback);
        _brushes.Clear();
    }

    /// <summary>Releases the renderer resources.</summary>
    public void Dispose()
        => _glyphs.Dispose();

    /// <summary>Renders a single row into <paramref name="drawingContext"/> (local origin = row top-left).</summary>
    public void RenderRow(DrawingContext drawingContext, Span<TerminalCellInfo> row, TerminalSelection? selection, int rowIndex, float pixelsPerDip)
    {
        double cellW = _glyphs.CellWidth;
        double cellH = _glyphs.CellHeight;
        double baselineY = _glyphs.Ascent;
        int len = row.Length;

        // Paint all cell backgrounds before any glyphs. A full-width CJK glyph can
        // extend across its continuation cell; painting that cell afterwards would
        // cover the right half of the glyph (most visible with nano's inverse video).
        int col = 0;
        while (col < len)
        {
            CellRenderStyle style = row[col].Style;
            bool selected = selection?.Contains(rowIndex, col) ?? false;
            int runStart = col;

            // Coalesce by both style and selection state so selected cells get the
            // highlight colors without bleeding into unselected neighbors.
            while (col < len && row[col].Style == style && (selection?.Contains(rowIndex, col) ?? false) == selected)
                col++;

            int runEnd = col;
            double x = runStart * cellW;

            (Color fg, Color bg) = ResolveColors(style, selected);
            if (!style.BackgroundIsDefault || style.Inverse || selected)
                drawingContext.DrawRectangle(Brush(bg), null, new Rect(x, 0, (runEnd - runStart) * cellW, cellH));
        }

        col = 0;
        while (col < len)
        {
            CellRenderStyle style = row[col].Style;
            bool selected = selection?.Contains(rowIndex, col) ?? false;
            int runStart = col;
            while (col < len && row[col].Style == style && (selection?.Contains(rowIndex, col) ?? false) == selected)
                col++;

            int runEnd = col;
            double x = runStart * cellW;
            (Color fg, _) = ResolveColors(style, selected);
            if (!style.Continuation)
                DrawGlyphRun(drawingContext, row, runStart, runEnd, x, baselineY, Brush(fg), style, pixelsPerDip);
        }
    }

    private void DrawGlyphRun(DrawingContext drawingContext, Span<TerminalCellInfo> row, int start, int end,
        double x, double baselineY, Brush fgBrush, CellRenderStyle style, float pixelsPerDip)
    {
        double cellW = _glyphs.CellWidth;

        GlyphTypeface? current = null;
        _chars.Clear();
        _glyphIndices.Clear();
        _advanceWidths.Clear();
        _clusterMap.Clear();
        Span<char> text = stackalloc char[2];
        int segCellCount = 0;
        double segX = x;

        void Flush()
        {
            if (current is null || _chars.Count == 0)
            {
                segCellCount = 0;
                _chars.Clear();
                _glyphIndices.Clear();
                _advanceWidths.Clear();
                _clusterMap.Clear();
                return;
            }

            double segWidth = segCellCount * cellW;

            // Snapshot immutable arrays for the GlyphRun. WPF stores the IList references
            // internally and may read them later during hit-testing/bounds computation, so
            // reusing and clearing the builder lists would corrupt previously drawn runs.
            ushort[] glyphIndices = _glyphIndices.ToArray();
            double[] advanceWidths = _advanceWidths.ToArray();
            char[] characters = _chars.ToArray();
            ushort[] clusterMap = _clusterMap.ToArray();

            GlyphRun run = new(
                current,
                bidiLevel: 0,
                isSideways: false,
                renderingEmSize: _emSize,
                pixelsPerDip: pixelsPerDip,
                glyphIndices: glyphIndices,
                baselineOrigin: new Point(segX, baselineY),
                advanceWidths: advanceWidths,
                glyphOffsets: null,
                characters: characters,
                deviceFontName: null,
                clusterMap: clusterMap,
                caretStops: null,
                language: XmlLanguage.GetLanguage("en-us"));

            drawingContext.DrawGlyphRun(fgBrush, run);

            // Some fixed-width CJK faces (including NSimSun) expose no bold face.
            // Slightly overdraw those regular glyphs so ANSI SGR 1 remains visible.
            if (style.Bold && current.Weight < FontWeights.SemiBold)
            {
                GlyphRun syntheticBoldRun = new(
                    current,
                    bidiLevel: 0,
                    isSideways: false,
                    renderingEmSize: _emSize,
                    pixelsPerDip: pixelsPerDip,
                    glyphIndices: glyphIndices,
                    baselineOrigin: new Point(segX + Math.Max(0.35, cellW * 0.035), baselineY),
                    advanceWidths: advanceWidths,
                    glyphOffsets: null,
                    characters: characters,
                    deviceFontName: null,
                    clusterMap: clusterMap,
                    caretStops: null,
                    language: XmlLanguage.GetLanguage("en-us"));
                drawingContext.DrawGlyphRun(fgBrush, syntheticBoldRun);
            }

            DrawDecorations(drawingContext, style, segX, segWidth, baselineY);

            segX += segWidth;
            _chars.Clear();
            _glyphIndices.Clear();
            _advanceWidths.Clear();
            _clusterMap.Clear();
            segCellCount = 0;
        }

        for (int i = start; i < end; i++)
        {
            ref readonly TerminalCellInfo cell = ref row[i];
            if (cell.Style.Continuation)
                continue;

            Rune rune = cell.Character;
            FontWeight weight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
            FontStyle fontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
            (GlyphTypeface tf, ushort glyph) = _glyphs.GetGlyph(rune, weight, fontStyle);

            if (current is not null && !ReferenceEquals(tf, current))
                Flush();

            current = tf;

            bool encoded = rune.TryEncodeToUtf16(text, out int charsWritten);
            Debug.Assert(encoded && charsWritten > 0);

            ushort glyphIndex = (ushort)_glyphIndices.Count;

            for (int c = 0; c < charsWritten; c++)
            {
                _chars.Add(text[c]);
                _clusterMap.Add(glyphIndex);
            }

            _glyphIndices.Add(glyph);
            int cellWidth = cell.Style.Wide ? 2 : 1;
            _advanceWidths.Add(cellW * cellWidth);
            segCellCount += cellWidth;
        }

        Flush();
    }

    private void DrawDecorations(DrawingContext drawingContext, CellRenderStyle style, double x, double width, double baselineY)
    {
        Color fg = style.ForegroundIsDefault ? _options.DefaultForeground : style.Foreground;
        Color decoColor = style.UnderlineColorIsDefault ? fg : style.UnderlineColor;
        Brush decoBrush = Brush(decoColor);
        double thickness = Math.Max(1, _glyphs.UnderlineThickness);
        Pen pen = new(decoBrush, thickness);

        if (style.UnderlineStyle != Underline.None || style.DoublyUnderlined)
        {
            double y = baselineY - _glyphs.UnderlinePosition;
            drawingContext.DrawLine(pen, new Point(x, y), new Point(x + width, y));

            if (style.UnderlineStyle == Underline.Double)
                drawingContext.DrawLine(pen, new Point(x, y - thickness - 1), new Point(x + width, y - thickness - 1));
        }

        if (style.Strikethrough)
        {
            double y = baselineY - _glyphs.StrikethroughPosition;
            drawingContext.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        }

        if (style.Overline)
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x + width, 0));
    }

    private (Color Fg, Color Bg) ResolveColors(CellRenderStyle style, bool selected)
    {
        Color fg = style.ForegroundIsDefault ? _options.DefaultForeground : style.Foreground;
        Color bg = style.BackgroundIsDefault ? _options.DefaultBackground : style.Background;

        if (style.Inverse)
            (fg, bg) = (bg, fg);

        if (style.Conceal)
            fg = bg;

        if (selected && style.ForegroundIsDefault && style.BackgroundIsDefault && !style.Inverse)
            (fg, bg) = (_options.DefaultBackground, _options.DefaultForeground);

        return (fg, bg);
    }

    private Brush Brush(Color color)
    {
        uint key = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
        if (!_brushes.TryGetValue(key, out Brush? brush))
        {
            brush = new SolidColorBrush(WindowsColor.FromArgb(color.A, color.R, color.G, color.B));
            _brushes[key] = brush;
        }

        return brush;
    }
}
