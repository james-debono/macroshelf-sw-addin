using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace MacroDeck
{
    // Builds the PNG icon strips SolidWorks 2016+ expects (one strip per size,
    // commands side by side), from user BMP/PNG files or generated fallbacks.
    internal static class IconFactory
    {
        private static readonly int[] StripSizes = { 20, 32, 40, 64, 96, 128 };

        public static string[] SaveStrips(List<Bitmap> icons, string dir, string baseName)
        {
            // Scale from premultiplied-alpha copies: interpolating straight
            // ARGB blends the RGB of transparent (black) pixels into every
            // antialiased edge, which visibly darkens icons that are mostly
            // thin strokes over transparency.
            List<Bitmap> premultiplied = new List<Bitmap>();
            try
            {
                foreach (Bitmap icon in icons)
                {
                    Bitmap p = new Bitmap(icon.Width, icon.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(p))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(icon, new Rectangle(0, 0, icon.Width, icon.Height));
                    }
                    premultiplied.Add(p);
                }

                string[] paths = new string[StripSizes.Length];
                for (int i = 0; i < StripSizes.Length; i++)
                {
                    int size = StripSizes[i];
                    using (Bitmap strip = new Bitmap(size * icons.Count, size, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics g = Graphics.FromImage(strip))
                        using (ImageAttributes attrs = new ImageAttributes())
                        {
                            // Without this, sampling at the image border mixes
                            // in "outside" pixels as transparent black.
                            attrs.SetWrapMode(WrapMode.TileFlipXY);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            for (int n = 0; n < premultiplied.Count; n++)
                            {
                                Bitmap source = premultiplied[n];
                                g.DrawImage(source, new Rectangle(n * size, 0, size, size),
                                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
                            }
                        }
                        string path = Path.Combine(dir, baseName + "_" + size + ".png");
                        strip.Save(path, ImageFormat.Png);
                        paths[i] = path;
                    }
                }
                return paths;
            }
            finally
            {
                foreach (Bitmap p in premultiplied)
                {
                    p.Dispose();
                }
            }
        }

        // Loads a user-supplied icon. For BMP files the corner pixel colour is
        // treated as transparent (classic toolbar-bitmap convention).
        public static Bitmap LoadUserIcon(string path)
        {
            try
            {
                Bitmap copy;
                using (Bitmap original = new Bitmap(path))
                {
                    copy = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.DrawImage(original, 0, 0, original.Width, original.Height);
                    }
                }
                if (string.Equals(Path.GetExtension(path), ".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    copy.MakeTransparent(copy.GetPixel(0, 0));
                }
                return copy;
            }
            catch
            {
                return null;
            }
        }

        // Fallback icon when a folder has no BMP: coloured tile with the first letter.
        public static Bitmap MakeTileIcon(string name)
        {
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            string letter = "?";
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    letter = char.ToUpperInvariant(c).ToString();
                    break;
                }
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (GraphicsPath tile = RoundedRect(new Rectangle(8, 8, 112, 112), 24))
                using (SolidBrush brush = new SolidBrush(TileColor(name)))
                {
                    g.FillPath(brush, tile);
                }
                using (Font font = new Font("Segoe UI", 64f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(letter, font, Brushes.White, new RectangleF(8, 10, 112, 112), format);
                }
            }
            return bmp;
        }

        // The MacroDeck library icon. Uses the artwork embedded at build time
        // (src\assets\library.png) when present, otherwise draws a fallback.
        public static Bitmap MakeLibraryIcon()
        {
            Bitmap embedded = LoadEmbedded("MacroDeck.library.png");
            if (embedded != null)
            {
                return embedded;
            }
            return DrawLibraryIcon();
        }

        public static Bitmap LoadEmbedded(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }
                    using (Bitmap raw = new Bitmap(stream))
                    {
                        // Copy: a Bitmap built on a stream needs that stream to
                        // stay open for its whole life.
                        Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(copy))
                        {
                            g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height));
                        }
                        return copy;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap DrawLibraryIcon()
        {
            // Three book spines on a shelf.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(21, 101, 192)))
                {
                    g.FillRectangle(b, 20, 26, 24, 80);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 81, 0)))
                {
                    g.FillRectangle(b, 52, 14, 24, 92);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(46, 125, 50)))
                {
                    g.FillRectangle(b, 84, 26, 24, 80);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 71, 79)))
                {
                    g.FillRectangle(b, 12, 106, 104, 12);
                }
            }
            return bmp;
        }

        // The Library flyout's item icons. Each uses the artwork embedded at
        // build time from src\assets\ when it is there, and falls back to a
        // drawn one when it is not - so the add-in still has a usable toolbar
        // if a PNG is ever removed, and a forker who drops in their own
        // artwork does not have to touch any code.
        public static Bitmap MakeSetupIcon()
        {
            return LoadEmbedded("MacroDeck.setup.png") ?? DrawSetupIcon();
        }

        public static Bitmap MakeGuideIcon()
        {
            return LoadEmbedded("MacroDeck.guide.png") ?? DrawGuideIcon();
        }

        public static Bitmap MakeScanIcon()
        {
            return LoadEmbedded("MacroDeck.scan.png") ?? DrawScanIcon();
        }

        public static Bitmap MakeUpdateIcon()
        {
            return LoadEmbedded("MacroDeck.update.png") ?? DrawUpdateIcon();
        }

        public static Bitmap MakeUpdateAvailableIcon()
        {
            return LoadEmbedded("MacroDeck.update-available.png") ?? DrawUpdateAvailableIcon();
        }

        private static Bitmap DrawSetupIcon()
        {
            // Folder glyph - Setup picks the library folder.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath tab = RoundedRect(new Rectangle(14, 26, 46, 26), 8))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(251, 140, 0)))
                {
                    g.FillPath(b, tab);
                }
                using (GraphicsPath body = RoundedRect(new Rectangle(14, 40, 100, 64), 10))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 167, 38)))
                {
                    g.FillPath(b, body);
                }
            }
            return bmp;
        }

        private static Bitmap DrawGuideIcon()
        {
            // Rounded tile with a question mark - the Library guide.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (GraphicsPath tile = RoundedRect(new Rectangle(8, 8, 112, 112), 24))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(69, 90, 120)))
                {
                    g.FillPath(brush, tile);
                }
                using (Font font = new Font("Segoe UI", 66f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString("?", font, Brushes.White, new RectangleF(8, 10, 112, 112), format);
                }
            }
            return bmp;
        }

        private static Bitmap DrawUpdateIcon()
        {
            // Cloud with a downward arrow - checking what is published.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color color = Color.FromArgb(84, 110, 142);
                using (SolidBrush b = new SolidBrush(color))
                {
                    // Three overlapping discs plus a bar make a cloud that
                    // still reads at 20 px, where a drawn outline would not.
                    g.FillEllipse(b, 20, 40, 44, 44);
                    g.FillEllipse(b, 48, 26, 52, 52);
                    g.FillEllipse(b, 76, 46, 34, 34);
                    g.FillRectangle(b, 38, 62, 72, 20);
                }
                using (Pen pen = new Pen(color, 13f))
                {
                    g.DrawLine(pen, 64, 74, 64, 104);
                }
                using (SolidBrush b = new SolidBrush(color))
                {
                    g.FillPolygon(b, new Point[] {
                        new Point(64, 118), new Point(44, 94), new Point(84, 94) });
                }
            }
            return bmp;
        }

        private static Bitmap DrawUpdateAvailableIcon()
        {
            // The same cloud in the alert colour, with a dot to catch the eye.
            // Only ever shown when there really is something newer.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color color = Color.FromArgb(46, 125, 50);
                using (SolidBrush b = new SolidBrush(color))
                {
                    g.FillEllipse(b, 14, 40, 44, 44);
                    g.FillEllipse(b, 42, 26, 52, 52);
                    g.FillEllipse(b, 70, 46, 34, 34);
                    g.FillRectangle(b, 32, 62, 72, 20);
                }
                using (Pen pen = new Pen(color, 13f))
                {
                    g.DrawLine(pen, 58, 74, 58, 104);
                }
                using (SolidBrush b = new SolidBrush(color))
                {
                    g.FillPolygon(b, new Point[] {
                        new Point(58, 118), new Point(38, 94), new Point(78, 94) });
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(216, 67, 21)))
                {
                    g.FillEllipse(b, 92, 6, 30, 30);
                }
            }
            return bmp;
        }

        private static Bitmap DrawScanIcon()
        {
            // Circular refresh arrows.
            Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color color = Color.FromArgb(21, 101, 192);
                Rectangle ring = new Rectangle(26, 26, 76, 76);
                using (Pen pen = new Pen(color, 14f))
                {
                    g.DrawArc(pen, ring, -60f, 145f);
                    g.DrawArc(pen, ring, 120f, 145f);
                }
                using (SolidBrush b = new SolidBrush(color))
                {
                    g.FillPolygon(b, new Point[] { new Point(38, 102), new Point(66, 90), new Point(66, 118) });
                    g.FillPolygon(b, new Point[] { new Point(90, 26), new Point(62, 14), new Point(62, 42) });
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static readonly Color[] Palette =
        {
            Color.FromArgb(198, 40, 40),
            Color.FromArgb(173, 20, 87),
            Color.FromArgb(106, 27, 154),
            Color.FromArgb(40, 53, 147),
            Color.FromArgb(21, 101, 192),
            Color.FromArgb(0, 131, 143),
            Color.FromArgb(46, 125, 50),
            Color.FromArgb(230, 81, 0),
            Color.FromArgb(78, 52, 46),
            Color.FromArgb(55, 71, 79)
        };

        private static Color TileColor(string name)
        {
            int hash = 0;
            foreach (char c in name.ToUpperInvariant())
            {
                hash = hash * 31 + c;
            }
            return Palette[Math.Abs(hash % Palette.Length)];
        }
    }
}
