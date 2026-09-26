using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace FamilyStudio.Rhino
{
    /// <summary>
    /// The Family Studio panel. For now: a hello-world cover sheet with a title block that shows
    /// whether the .env file and the ChatGPT sign-in are in place. Everything is
    /// drawn on one canvas, so it looks the same in Rhino for Windows and for Mac, light or dark.
    /// Rhino needs the Guid attribute and a public parameterless constructor to host the panel.
    /// </summary>
    [Guid("6a645485-1efa-4764-a43e-4579affec9d1")]
    public sealed class HelloPanel : Panel
    {
        public const string Caption = "Family Studio";
        public static Guid PanelId => typeof(HelloPanel).GUID;

        private readonly Drawable _sheet = new Drawable();
        private Theme _theme = Theme.Current();
        private EnvStatus _env = EnvStatus.Load();

        public HelloPanel()
        {
            MinimumSize = new Size(240, 420);
            _sheet.Paint += (_, e) => Draw(e.Graphics, _sheet.Width, _sheet.Height);
            _sheet.SizeChanged += (_, _) => _sheet.Invalidate();
            _sheet.MouseDoubleClick += (_, _) => Refresh(); // re-read .env and the sign-in without restarting Rhino
            Content = _sheet;
            RhinoApp.AppSettingsChanged += OnAppSettingsChanged;
        }

        private void OnAppSettingsChanged(object? sender, EventArgs e) => Application.Instance.AsyncInvoke(Refresh);

        private void Refresh()
        {
            _theme = Theme.Current();
            _env = EnvStatus.Load();
            _sheet.Invalidate();
        }

        private void Draw(Graphics g, float width, float height)
        {
            var t = _theme;
            g.AntiAlias = true;
            g.FillRectangle(t.Bg, 0, 0, width, height);
            const float margin = 20;
            var inner = Math.Max(120, width - 2 * margin);

            // Header: mark and wordmark, centred on the mark.
            g.FillRectangle(t.Surface, 0, 0, width, 46);
            Mark(g, margin, 12, 22);
            var wordmark = Type.Label(9.5f);
            var caps = Type.Caps("Family Studio");
            g.DrawText(wordmark, t.Ink, margin + 32, 23 - g.MeasureString(wordmark, caps).Height / 2, caps);
            g.FillRectangle(t.Rule, 0, 46, width, 1);

            // The message.
            var y = 78f;
            var display = Type.Display(24);
            g.DrawText(display, t.Ink, margin, y, "Hello, World.");
            y += g.MeasureString(display, "Hello, World.").Height + 10;
            y = Paragraph(g, Type.Text(10), t.Ink2, margin, y, inner,
                "Family Studio for Rhino is in development. The Revit plug-in already turns a description or a product photo into a native family; this panel is where the Rhino version will live.");

            // Construction linework: an isometric box on a dimension string, drawn in non-photo blue.
            var titleBlockTop = height - 124;
            var art = Math.Min(inner * 0.62f, Math.Max(0, titleBlockTop - y - 40));
            if (art > 60) Isometric(g, margin + (inner - art) / 2, y + 20, art);

            // Title block.
            g.FillRectangle(t.Surface, 0, titleBlockTop, width, height - titleBlockTop);
            g.FillRectangle(t.RuleStrong, 0, titleBlockTop, width, 1);
            Cell(g, margin, titleBlockTop + 10, "Status", "Preliminary", t.Ink2, mono: false);
            g.FillRectangle(t.Rule, margin, titleBlockTop + 50, inner, 1);

            var row = titleBlockTop + 60;
            Status(g, margin, row, inner, ".env file", _env.LoadedFile is null ? "Not found. Using defaults." : "Loaded", _env.LoadedFile is not null);
            Status(g, margin, row + 26, inner, "ChatGPT sign-in", _env.ChatGptSignedIn ? "Signed in (shared with Revit)" : "Not signed in yet", _env.ChatGptSignedIn);
        }

        private void Cell(Graphics g, float x, float y, string label, string value, Color color, bool mono)
        {
            g.DrawText(Type.Label(7), _theme.Ink3, x, y, Type.Caps(label));
            g.DrawText(mono ? Type.Mono(9) : Type.Label(9), color, x, y + 15, mono ? value : Type.Caps(value));
        }

        private void Status(Graphics g, float x, float y, float width, string label, string value, bool ok)
        {
            g.FillEllipse(ok ? _theme.Success : _theme.Warning, x, y + 5, 6, 6);
            g.DrawText(Type.Label(7.5f), _theme.Ink3, x + 14, y + 1, Type.Caps(label));
            var font = Type.Text(9);
            var size = g.MeasureString(font, value);
            g.DrawText(font, _theme.Ink, x + width - size.Width, y - 1, value);
        }

        private void Mark(Graphics g, float x, float y, float s)
        {
            PointF P(float px, float py) => new PointF(x + px * s / 22, y + py * s / 22);
            var ink = new Pen(_theme.Ink, 1.2f);
            Face(g, ink, _theme.Surface, P(11, 2), P(20, 6.5f), P(11, 11), P(2, 6.5f));
            Face(g, ink, _theme.Accent, P(2, 6.5f), P(11, 11), P(11, 20.5f), P(2, 16));
            Face(g, ink, _theme.Rule, P(11, 11), P(20, 6.5f), P(20, 16), P(11, 20.5f));
        }

        private static void Face(Graphics g, Pen ink, Color fill, params PointF[] points)
        {
            g.FillPolygon(fill, points);
            g.DrawPolygon(ink, points);
        }

        private void Isometric(Graphics g, float x, float y, float size)
        {
            var pen = new Pen(_theme.Construction, 1f);
            PointF P(float px, float py) => new PointF(x + px * size, y + py * size * 0.9f);
            // A box in isometric: top, front and side faces as linework only.
            g.DrawPolygon(pen, P(0.5f, 0.05f), P(0.95f, 0.3f), P(0.5f, 0.55f), P(0.05f, 0.3f));
            g.DrawLine(pen, P(0.05f, 0.3f), P(0.05f, 0.72f));
            g.DrawLine(pen, P(0.5f, 0.55f), P(0.5f, 0.97f));
            g.DrawLine(pen, P(0.95f, 0.3f), P(0.95f, 0.72f));
            g.DrawLine(pen, P(0.05f, 0.72f), P(0.5f, 0.97f));
            g.DrawLine(pen, P(0.5f, 0.97f), P(0.95f, 0.72f));
            // Its width as a dimension string, ticks at 45 degrees.
            var ink = new Pen(_theme.Ink, 1f);
            var a = P(0.05f, 0.86f);
            var b = P(0.5f, 1.11f);
            g.DrawLine(ink, a, b);
            foreach (var p in new[] { a, b }) g.DrawLine(ink, p.X - 4, p.Y + 4, p.X + 4, p.Y - 4);
        }

        /// <summary>Draws wrapped text and returns the y below it.</summary>
        private static float Paragraph(Graphics g, Font font, Color color, float x, float y, float width, string text)
        {
            var line = "";
            var height = g.MeasureString(font, "Ag").Height;
            var lines = new List<string>();
            foreach (var word in text.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (g.MeasureString(font, candidate).Width > width && line.Length > 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            if (line.Length > 0) lines.Add(line);
            foreach (var l in lines)
            {
                g.DrawText(font, color, x, y, l);
                y += height * 1.25f;
            }
            return y;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) RhinoApp.AppSettingsChanged -= OnAppSettingsChanged;
            base.Dispose(disposing);
        }
    }
}
