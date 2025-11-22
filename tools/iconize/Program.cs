using System;
using System.Collections.Generic;
// iconize: simple PNG -> multi-size ICO generator with transparency heuristics
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Iconize
{
    class Program
    {
        static int[] Sizes = new int[] { 256, 48, 32, 16 };

        static int Main(string[] args)
        {
            try
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: iconize <source-png> <dest-ico>");
                    return 2;
                }
                var src = args[0];
                var dst = args[1];
                if (!File.Exists(src))
                {
                    Console.Error.WriteLine($"Source PNG not found: {src}");
                    return 3;
                }

                var pngImages = new List<byte[]>();

                using (var srcBmpOrig = (Bitmap)Image.FromFile(src))
                using (var srcBmp = CreateSourceWithTransparency(srcBmpOrig))
                {
                    foreach (var s in Sizes)
                    {
                        using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.CompositingQuality = CompositingQuality.HighQuality;
                                g.SmoothingMode = SmoothingMode.HighQuality;
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.Clear(Color.Transparent);
                                g.DrawImage(srcBmp, 0, 0, s, s);
                            }
                            using (var ms = new MemoryStream())
                            {
                                bmp.Save(ms, ImageFormat.Png);
                                pngImages.Add(ms.ToArray());
                            }
                        }
                    }
                }

                using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((ushort)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)pngImages.Count);

                    int imageDataOffset = 6 + (16 * pngImages.Count);
                    for (int i = 0; i < pngImages.Count; i++)
                    {
                        var data = pngImages[i];
                        int size = Sizes[i];
                        byte width = (byte)(size >= 256 ? 0 : size);
                        byte height = (byte)(size >= 256 ? 0 : size);
                        bw.Write(width);
                        bw.Write(height);
                        bw.Write((byte)0);
                        bw.Write((byte)0);
                        bw.Write((ushort)1);
                        bw.Write((ushort)32);
                        bw.Write((uint)data.Length);
                        bw.Write((uint)imageDataOffset);
                        imageDataOffset += data.Length;
                    }

                    for (int i = 0; i < pngImages.Count; i++)
                    {
                        bw.Write(pngImages[i]);
                    }
                }

                Console.WriteLine($"Wrote ICO: {dst}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 100;
            }
        }

        private static Bitmap CreateSourceWithTransparency(Bitmap orig)
        {
            var src = new Bitmap(orig.Width, orig.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(src))
            {
                g.DrawImage(orig, 0, 0, orig.Width, orig.Height);
            }

            // If any pixel already has alpha, assume transparency present
            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    if (src.GetPixel(x, y).A != 255) return src;
                }
            }

            int tile = 8;
            long[] sumR = new long[2], sumG = new long[2], sumB = new long[2], cnt = new long[2];
            for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int p = ((x / tile) + (y / tile)) & 1;
                var c = src.GetPixel(x, y);
                sumR[p] += c.R; sumG[p] += c.G; sumB[p] += c.B; cnt[p]++;
            }
            if (cnt[0] == 0 || cnt[1] == 0) return src;

            var avg0 = Color.FromArgb((int)(sumR[0] / cnt[0]), (int)(sumG[0] / cnt[0]), (int)(sumB[0] / cnt[0]));
            var avg1 = Color.FromArgb((int)(sumR[1] / cnt[1]), (int)(sumG[1] / cnt[1]), (int)(sumB[1] / cnt[1]));
            double diff = ColorDistance(avg0, avg1);
            if (diff < 30) return src;

            var outBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                double d0 = ColorDistance(c, avg0);
                double d1 = ColorDistance(c, avg1);
                double mind = Math.Min(d0, d1);
                if (mind < 40)
                {
                    int alpha = (int)(Math.Min(1.0, mind / 40.0) * 255);
                    alpha = Math.Max(0, Math.Min(255, alpha));
                    outBmp.SetPixel(x, y, Color.FromArgb(alpha, c.R, c.G, c.B));
                }
                else outBmp.SetPixel(x, y, c);
            }
            return outBmp;
        }

        private static double ColorDistance(Color a, Color b)
        {
            int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }
    }
}
