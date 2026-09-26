using System;
using Eto.Drawing;

namespace FamilyStudio.Rhino
{
    /// <summary>
    /// The Family Studio design tokens, the same values the Revit window uses: PAPER when Rhino is
    /// light and NIGHT DRAFTING when it is dark. One cobalt accent; construction blue for linework.
    /// </summary>
    internal sealed class Theme
    {
        public Color Bg, Surface, Rule, RuleStrong, Ink, Ink2, Ink3, Accent, Construction, Success, Warning;

        public static Theme Paper => new Theme
        {
            Bg = Hex("#F4F2ED"), Surface = Hex("#FAF9F6"), Rule = Hex("#DEDAD1"), RuleStrong = Hex("#8C877C"),
            Ink = Hex("#1B1A17"), Ink2 = Hex("#4F4B44"), Ink3 = Hex("#6A655B"),
            Accent = Hex("#2B45CF"), Construction = Hex("#3D8FC6"), Success = Hex("#2E6B3F"), Warning = Hex("#7A4D00")
        };

        public static Theme Night => new Theme
        {
            Bg = Hex("#1C2027"), Surface = Hex("#242932"), Rule = Hex("#3A414E"), RuleStrong = Hex("#808BA0"),
            Ink = Hex("#EEF0F3"), Ink2 = Hex("#B6BDC9"), Ink3 = Hex("#9CA4B1"),
            Accent = Hex("#8FA3FF"), Construction = Hex("#A4DDED"), Success = Hex("#74CE90"), Warning = Hex("#E8B754")
        };

        public static Theme Current()
        {
            try { return global::Rhino.Runtime.HostUtils.RunningInDarkMode ? Night : Paper; }
            catch (Exception) { return Paper; }
        }

        private static Color Hex(string hex) => Color.Parse(hex);
    }

    /// <summary>
    /// Type roles. Bahnschrift (DIN 1451, the lettering of technical drawings) for tracked labels,
    /// Segoe UI for text, Cascadia Mono or Consolas for sheet numbers. All ship with Windows.
    /// </summary>
    internal static class Type
    {
        public static Font Display(float size) => Make(new[] { "Segoe UI Variable Display Light", "Segoe UI Light", "Segoe UI" }, size, FontStyle.None);
        public static Font Text(float size) => Make(new[] { "Segoe UI Variable Text", "Segoe UI" }, size, FontStyle.None);
        public static Font Label(float size) => Make(new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI" }, size, FontStyle.None);
        public static Font Mono(float size) => Make(new[] { "Cascadia Mono", "Consolas", "Menlo" }, size, FontStyle.None);

        private static Font Make(string[] families, float size, FontStyle style)
        {
            foreach (var family in families)
            {
                try
                {
                    var font = new Font(family, size, style);
                    if (string.Equals(font.FamilyName, family, StringComparison.OrdinalIgnoreCase) || family == families[families.Length - 1])
                        return font;
                }
                catch (Exception) { /* try the next family */ }
            }
            return SystemFonts.Default(size);
        }

        /// <summary>Upper-case with a hair space between letters: tracked capitals without letter-spacing support.</summary>
        public static string Caps(string text)
        {
            var upper = text.ToUpperInvariant();
            var builder = new System.Text.StringBuilder(upper.Length * 2);
            for (var i = 0; i < upper.Length; i++)
            {
                builder.Append(upper[i]);
                if (i < upper.Length - 1) builder.Append(' ');
            }
            return builder.ToString();
        }
    }
}
