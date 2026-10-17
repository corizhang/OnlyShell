using System.Text;
using System.Windows;
using System.Windows.Media;

namespace VirtualTerminal.Rendering;

/// <summary>
/// Resolves <see cref="GlyphTypeface"/> instances for the primary font and fallback families,
/// maps codepoints to glyph indices (with fallback), and exposes font metrics for grid layout.
/// Glyph lookups are cached per codepoint.
/// </summary>
public sealed class GlyphCache : IDisposable
{
    private static readonly FontFamily _defaultFont = new FontFamily("Consolas");

    private readonly List<FontFamily> _families = [];
    private readonly Dictionary<(int Codepoint, FontWeight Weight, FontStyle Style), (GlyphTypeface Tf, ushort Glyph)> _cache = [];
    private readonly Dictionary<(int FamilyIndex, FontWeight Weight, FontStyle Style), GlyphTypeface> _typefaces = [];

    private GlyphTypeface? _primary;
    private FontFamily _fontFamily = _defaultFont;

    private double _emSize;
    private double _scale = 1.0;

    private double _cellWidth;
    private double _cellHeight;
    private double _ascent;
    private double _underlinePos;
    private double _underlineThickness;
    private double _strikePos;
    private double _strikeThickness;

    /// <summary>Gets a value indicating whether the cache has been configured with a valid primary typeface.</summary>
    public bool IsValid => _primary is not null;

    /// <summary>Gets the advance width of a single cell in pixels.</summary>
    public double CellWidth => _cellWidth;

    /// <summary>Gets the height of a single cell in pixels.</summary>
    public double CellHeight => _cellHeight;

    /// <summary>Gets the typographic ascent used to position glyphs within the cell.</summary>
    public double Ascent => _ascent;

    /// <summary>Gets the underline offset from the baseline.</summary>
    public double UnderlinePosition => _underlinePos;

    /// <summary>Gets the recommended underline thickness.</summary>
    public double UnderlineThickness => _underlineThickness;

    /// <summary>Gets the strikethrough offset from the baseline.</summary>
    public double StrikethroughPosition => _strikePos;

    /// <summary>Gets the recommended strikethrough thickness.</summary>
    public double StrikethroughThickness => _strikeThickness;

    /// <summary>Gets the primary glyph typeface used for rendering.</summary>
    public GlyphTypeface Primary => _primary ?? throw new InvalidOperationException("GlyphCache not configured.");

    /// <summary>Rebuilds the cache for the given primary family, size, line-height, and fallbacks.</summary>
    public void Configure(string family, double emSize, double lineHeight, IReadOnlyList<string>? fallback)
    {
        _families.Clear();
        _cache.Clear();
        _typefaces.Clear();

        _emSize = emSize;
        _fontFamily = string.IsNullOrWhiteSpace(family)
            ? _defaultFont : new FontFamily(family);
        _families.Add(_fontFamily);

        _primary = ResolveTypeface(_fontFamily, FontWeights.Normal, FontStyles.Normal, FontStretches.Normal);

        if (fallback is { Count: > 0 })
        {
            foreach (string? f in fallback)
            {
                if (string.IsNullOrWhiteSpace(f))
                    continue;

                _families.Add(new FontFamily(f));
            }
        }

        MeasureMetrics(lineHeight);
    }

    private static GlyphTypeface ResolveTypeface(FontFamily fontFamily, FontWeight weight, FontStyle style, FontStretch stretch)
    {
        Typeface typeface = new(fontFamily, style, weight, stretch);
        if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface) || glyphTypeface is null)
            throw new InvalidOperationException($"Unable to resolve glyph typeface for '{fontFamily.Source}'.");

        return glyphTypeface;
    }

    private void MeasureMetrics(double lineHeight)
    {
        GlyphTypeface tf = Primary;
        _scale = _emSize;

        ushort rawGlyph = GetRawGlyph(tf, 'M');
        double advance = tf.AdvanceWidths[rawGlyph];

        if (advance <= 0)
            advance = 0.6;

        double lineSpacing = _fontFamily.LineSpacing > 0 ? _fontFamily.LineSpacing : 1.0;

        _cellWidth = advance * _scale;
        _underlinePos = tf.UnderlinePosition * _scale;
        _strikePos = tf.StrikethroughPosition * _scale;

        _ascent = Math.Clamp(tf.Baseline * _scale, 0, int.MaxValue);
        _cellHeight = Math.Ceiling(lineSpacing * _scale * Math.Max(0.5, lineHeight));

        _underlineThickness = Math.Max(1, tf.UnderlineThickness * _scale);
        _strikeThickness = Math.Max(1, tf.StrikethroughThickness * _scale);
    }

    /// <summary>Resolves the (typeface, glyph) for a codepoint, trying fallbacks when absent.</summary>
    public (GlyphTypeface Tf, ushort Glyph) GetGlyph(Rune rune)
        => GetGlyph(rune, FontWeights.Normal, FontStyles.Normal);

    /// <summary>Resolves the (typeface, glyph) for a codepoint and requested weight/style, trying fallbacks when absent.</summary>
    public (GlyphTypeface Tf, ushort Glyph) GetGlyph(Rune rune, FontWeight weight, FontStyle style)
    {
        int cp = rune.Value;
        var key = (cp, weight, style);
        if (_cache.TryGetValue(key, out (GlyphTypeface Tf, ushort Glyph) hit))
            return hit;

        GlyphTypeface primary = GetTypeface(0, weight, style);
        for (int i = 0; i < _families.Count; i++)
        {
            GlyphTypeface candidate = i == 0 ? primary : GetTypeface(i, weight, style);
            ushort glyph = GetRawGlyph(candidate, cp);
            if (glyph != 0)
                return _cache[key] = (candidate, glyph);
        }

        // Defensive: use the primary face's missing-glyph glyph if no family has the codepoint.
        return _cache[key] = (primary, 0);
    }

    private GlyphTypeface GetTypeface(int familyIndex, FontWeight weight, FontStyle style)
    {
        var key = (familyIndex, weight, style);
        if (_typefaces.TryGetValue(key, out GlyphTypeface? cached))
            return cached;

        GlyphTypeface typeface = ResolveTypeface(_families[familyIndex], weight, style, FontStretches.Normal);
        _typefaces[key] = typeface;
        return typeface;
    }

    private static ushort GetRawGlyph(GlyphTypeface tf, int codepoint)
    {
        if (tf.GlyphCount == 0)
            return 0;

        if (tf.CharacterToGlyphMap.TryGetValue(codepoint, out ushort glyph))
            return glyph;

        return 0;
    }

    /// <summary>Releases the glyph cache resources.</summary>
    public void Dispose()
    {
        _primary = null;
        _families.Clear();
        _cache.Clear();
        _typefaces.Clear();
    }
}
