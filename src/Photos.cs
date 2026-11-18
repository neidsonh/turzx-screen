// Porta-retrato: rodizio de fotos de uma pasta. As fotos sao preparadas em segundo plano,
// ja no tamanho da tela, para o desenho nao travar com arquivos grandes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TurzxScreen
{
    class PhotoFrameImage
    {
        public Bitmap Image; public string Caption = ""; public int W, H; public bool Fill;
    }

    static class PhotoFrame
    {
        public static volatile bool Enabled;
        public static volatile string Folder = "";
        public static volatile bool Recursive = true, Shuffle = true, ShowClock = true;
        public static volatile int Seconds = 30;
        public static volatile PhotoFrameImage Current;
        public static volatile string Status = "";
        public static volatile int Count;

        const long MaxFile = 80L * 1024 * 1024;
        static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        static readonly object sync = new object();
        static int wantW, wantH; static bool wantFill; static DateTime wanted = DateTime.MinValue;
        static volatile bool skip;
        static Thread th;

        public static void Start()
        {
            if (th != null) return;
            th = new Thread(Loop); th.IsBackground = true; th.Priority = ThreadPriority.BelowNormal; th.Start();
        }

        public static void Want(int w, int h, bool fill)
        {
            lock (sync) { wantW = w; wantH = h; wantFill = fill; wanted = DateTime.Now; }
        }

        public static void Next() { skip = true; }

        static void Loop()
        {
            var files = new List<string>(); var bag = new List<string>(); var rnd = new Random();
            string scanned = null; bool scannedSub = false; DateTime nextScan = DateTime.MinValue, nextPhoto = DateTime.MinValue;
            string shown = null;
            while (true)
            {
                try
                {
                    int w, h; bool fill; DateTime last;
                    lock (sync) { w = wantW; h = wantH; fill = wantFill; last = wanted; }
                    string folder = (Folder ?? "").Trim();
                    if (!Enabled || folder == "" || w == 0 || (DateTime.Now - last).TotalSeconds > 5) { Thread.Sleep(300); continue; }

                    if (folder != scanned || Recursive != scannedSub || DateTime.Now >= nextScan)
                    {
                        bool changed = folder != scanned || Recursive != scannedSub;
                        files = Scan(folder, Recursive); scanned = folder; scannedSub = Recursive; nextScan = DateTime.Now.AddMinutes(10);
                        Count = files.Count; bag.Clear();
                        Status = files.Count == 0 ? "Nenhuma foto encontrada nessa pasta." : files.Count + " fotos.";
                        if (changed) { nextPhoto = DateTime.MinValue; shown = null; Current = null; }
                    }
                    if (files.Count == 0) { Thread.Sleep(1000); continue; }

                    var cur = Current;
                    bool sizeChanged = cur != null && (cur.W != w || cur.H != h || cur.Fill != fill);
                    if (sizeChanged && shown != null)
                    {
                        var again = Compose(shown, w, h, fill);
                        if (again != null) Current = again;
                    }
                    if (skip || DateTime.Now >= nextPhoto)
                    {
                        skip = false;
                        for (int tries = 0; tries < 12; tries++)
                        {
                            if (bag.Count == 0) { bag.AddRange(files); if (!Shuffle) bag.Reverse(); }
                            int i = Shuffle ? rnd.Next(bag.Count) : bag.Count - 1;
                            string f = bag[i]; bag.RemoveAt(i);
                            if (f == shown && files.Count > 1) continue;
                            var img = Compose(f, w, h, fill);
                            if (img == null) continue;
                            Current = img; shown = f; break;
                        }
                        nextPhoto = DateTime.Now.AddSeconds(Math.Max(5, Seconds));
                    }
                }
                catch (Exception ex) { Status = "Erro ao ler fotos."; Log.Error("fotos", ex); Thread.Sleep(2000); }
                Thread.Sleep(200);
            }
        }

        static List<string> Scan(string root, bool recursive)
        {
            var r = new List<string>(); var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && r.Count < 50000)
            {
                string d = pending.Pop();
                try
                {
                    foreach (var f in Directory.GetFiles(d))
                        if (Array.IndexOf(Exts, Path.GetExtension(f).ToLowerInvariant()) >= 0) r.Add(f);
                    if (recursive) foreach (var s in Directory.GetDirectories(d)) pending.Push(s);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            r.Sort(StringComparer.OrdinalIgnoreCase);
            return r;
        }

        static void ApplyOrientation(Image img)
        {
            try
            {
                if (Array.IndexOf(img.PropertyIdList, 0x0112) < 0) return;
                switch (BitConverter.ToUInt16(img.GetPropertyItem(0x0112).Value, 0))
                {
                    case 2: img.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: img.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: img.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                    case 5: img.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: img.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: img.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: img.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch { }
        }

        static string TakenCaption(Image img)
        {
            try
            {
                foreach (int id in new[] { 0x9003, 0x0132 })
                {
                    if (Array.IndexOf(img.PropertyIdList, id) < 0) continue;
                    string s = Encoding.ASCII.GetString(img.GetPropertyItem(id).Value).Trim('\0', ' ');
                    DateTime d;
                    if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d.Year > 1980)
                        return d.ToString("MMMM 'de' yyyy", new CultureInfo("pt-BR"));
                }
            }
            catch { }
            return "";
        }

        static PhotoFrameImage Compose(string path, int w, int h, bool fill)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFile) return null;
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var img = Image.FromStream(ms, false, false))
                {
                    string caption = TakenCaption(img);
                    ApplyOrientation(img);
                    var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Black);
                        double sx = (double)w / img.Width, sy = (double)h / img.Height;
                        if (!fill)
                        {
                            // fundo: a propria foto ampliada e desfocada, para nao sobrar faixa preta
                            using (var tiny = new Bitmap(16, 16))
                            {
                                using (var t = Graphics.FromImage(tiny)) { t.InterpolationMode = InterpolationMode.HighQualityBilinear; t.DrawImage(img, 0, 0, 16, 16); }
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.DrawImage(tiny, new Rectangle(-w / 8, -h / 8, w + w / 4, h + h / 4));
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            }
                            using (var dark = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(dark, 0, 0, w, h);
                        }
                        double s = fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
                        int dw = (int)Math.Ceiling(img.Width * s), dh = (int)Math.Ceiling(img.Height * s);
                        g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
                    }
                    return new PhotoFrameImage { Image = dst, Caption = caption, W = w, H = h, Fill = fill };
                }
            }
            catch (OutOfMemoryException) { return null; } // formato invalido no GDI+
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
