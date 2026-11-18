// Acha as areas que mudaram entre dois quadros RGB565. A tela recebe ~170 KB/s, entao
// separar areas distantes vale muito mais do que os 6 bytes de cada comando extra.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace TurzxScreen
{
    static class DirtyRects
    {
        const int Band = 8;        // altura das faixas analisadas
        const int Gap = 24;        // colunas iguais toleradas dentro de uma mesma area
        const int MaxWaste = 768;  // pixels iguais aceitos ao juntar duas areas
        const int MaxRects = 24;   // limite de comandos por quadro

        public static List<Rectangle> Find(byte[] cur, byte[] prev, int w, int h)
        {
            var rects = new List<Rectangle>();
            var cols = new bool[w];
            for (int y = 0; y < h; y += Band)
            {
                int yEnd = Math.Min(h, y + Band);
                if (!MarkColumns(cur, prev, w, y, yEnd, cols)) continue;
                int x = 0;
                while (x < w)
                {
                    if (!cols[x]) { x++; continue; }
                    int end = x;
                    for (int j = x + 1; j < w && j - end <= Gap; j++) if (cols[j]) end = j;
                    AddSegment(rects, new Rectangle(x, y, end - x + 1, yEnd - y));
                    x = end + 1;
                }
            }
            while (rects.Count > MaxRects) MergeCheapest(rects);
            for (int i = 0; i < rects.Count; i++) rects[i] = Tighten(cur, prev, w, rects[i]);
            rects.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            return rects;
        }

        static bool MarkColumns(byte[] cur, byte[] prev, int w, int y0, int y1, bool[] cols)
        {
            Array.Clear(cols, 0, w);
            bool any = false;
            for (int y = y0; y < y1; y++)
            {
                int i = y * w * 2;
                for (int x = 0; x < w; x++, i += 2)
                    if (cur[i] != prev[i] || cur[i + 1] != prev[i + 1]) { cols[x] = true; any = true; }
            }
            return any;
        }

        static long Area(Rectangle r) { return (long)r.Width * r.Height; }

        static void AddSegment(List<Rectangle> rects, Rectangle seg)
        {
            for (int i = rects.Count - 1; i >= 0; i--)
            {
                var r = rects[i];
                if (r.Bottom != seg.Top || r.Right + Gap < seg.Left || seg.Right + Gap < r.Left) continue;
                var u = Rectangle.Union(r, seg);
                if (Area(u) - Area(r) - Area(seg) > MaxWaste) continue;
                rects[i] = u;
                return;
            }
            rects.Add(seg);
        }

        static void MergeCheapest(List<Rectangle> rects)
        {
            int bestA = 0, bestB = 1; long best = long.MaxValue;
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                {
                    long cost = Area(Rectangle.Union(rects[a], rects[b])) - Area(rects[a]) - Area(rects[b]);
                    if (cost < best) { best = cost; bestA = a; bestB = b; }
                }
            rects[bestA] = Rectangle.Union(rects[bestA], rects[bestB]);
            rects.RemoveAt(bestB);
        }

        static Rectangle Tighten(byte[] cur, byte[] prev, int w, Rectangle r)
        {
            int x0 = r.Left, x1 = r.Right - 1, y0 = r.Top, y1 = r.Bottom - 1;
            while (y0 < y1 && Same(cur, prev, w, x0, x1, y0, y0)) y0++;
            while (y1 > y0 && Same(cur, prev, w, x0, x1, y1, y1)) y1--;
            while (x0 < x1 && Same(cur, prev, w, x0, x0, y0, y1)) x0++;
            while (x1 > x0 && Same(cur, prev, w, x1, x1, y0, y1)) x1--;
            return new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        static bool Same(byte[] cur, byte[] prev, int w, int x0, int x1, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                int i = (y * w + x0) * 2, e = (y * w + x1) * 2 + 1;
                for (; i <= e; i++) if (cur[i] != prev[i]) return false;
            }
            return true;
        }
    }
}
