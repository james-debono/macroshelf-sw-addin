using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using MacroShelf;

// Packs the MacroShelf library glyph into a multi-size .ico (PNG-compressed
// entries, supported since Vista) for the MSI's Add/Remove Programs icon.
internal static class GenIcon
{
    // Uses the supplied artwork when given, otherwise the drawn fallback.
    private static Bitmap LoadMaster(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            using (Bitmap raw = new Bitmap(path))
            {
                Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(copy))
                {
                    g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height));
                }
                Console.WriteLine("Source: " + path);
                return copy;
            }
        }
        Console.WriteLine("Source: drawn fallback");
        return IconFactory.MakeLibraryIcon();
    }

    private static int Main(string[] args)
    {
        string outPath = args[0];
        string sourcePng = args.Length > 1 ? args[1] : null;
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> pngs = new List<byte[]>();
        using (Bitmap master = LoadMaster(sourcePng))
        {
            foreach (int size in sizes)
            {
                using (Bitmap frame = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(master, new Rectangle(0, 0, size, size));
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        frame.Save(ms, ImageFormat.Png);
                        pngs.Add(ms.ToArray());
                    }
                }
            }
        }

        using (FileStream fs = new FileStream(outPath, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type: icon
            w.Write((ushort)sizes.Length);   // image count
            int offset = 6 + (16 * sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                w.Write((byte)(size == 256 ? 0 : size)); // width (0 = 256)
                w.Write((byte)(size == 256 ? 0 : size)); // height
                w.Write((byte)0);            // palette colours
                w.Write((byte)0);            // reserved
                w.Write((ushort)1);          // colour planes
                w.Write((ushort)32);         // bits per pixel
                w.Write(pngs[i].Length);     // data size
                w.Write(offset);             // data offset
                offset += pngs[i].Length;
            }
            foreach (byte[] png in pngs)
            {
                w.Write(png);
            }
        }
        Console.WriteLine("Wrote " + outPath);
        return 0;
    }
}
