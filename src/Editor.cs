// Editor visual: temas proprios feitos de widgets, salvos em layout.json.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TurzxScreen
{
    public class Widget
    {
        public string Type = "text", Source = "cpu", Text = "Texto", Color = "", Path = "";
        public int X, Y, W = 100, H = 60, Font, Align;
        public bool Bg = true, Label = true, Seconds, Fill = true, Bar = true, Detail, Bold, Thin;
        public Rectangle Rect { get { return new Rectangle(X, Y, W, H); } }
    }

    public class CustomTheme
    {
        public string name = "Meu layout";
        public int baseTheme = -1; // tema pronto de onde saiu, ou -1
        public List<Widget> landscape, portrait;
    }

    // landscape/portrait na raiz: formato antigo, so lido para migrar
    public class LayoutFile { public List<Widget> landscape, portrait; public List<CustomTheme> themes; }

    static class LayoutStore
    {
        public const int First = 11; // indice do primeiro tema proprio (8 ate a versao 1, 10 na 2, 11 desde o tema VPN)
        static volatile List<CustomTheme> themes = new List<CustomTheme> { new CustomTheme() };
        public static volatile bool Dirty;
        static string FilePath { get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "layout.json"); } }

        static CustomTheme At(int theme)
        {
            var l = themes; int i = theme - First;
            return l[i >= 0 && i < l.Count ? i : 0];
        }

        public static string Name(int theme) { return At(theme).name; }
        public static int BaseTheme(int theme) { return At(theme).baseTheme; }

        public static string[] Names()
        {
            var r = new List<string>(); foreach (var t in themes) r.Add(t.name);
            return r.ToArray();
        }

        public static string UniqueName(string name)
        {
            var used = new HashSet<string>(Names(), StringComparer.OrdinalIgnoreCase);
            string s = name;
            for (int i = 2; used.Contains(s); i++) s = name + " " + i;
            return s;
        }

        public static List<Widget> Get(int theme, bool landscape)
        {
            var t = At(theme);
            var l = landscape ? t.landscape : t.portrait;
            if (l == null) { l = Default(landscape); Set(theme, landscape, l); }
            return l;
        }

        public static void Set(int theme, bool landscape, List<Widget> items)
        {
            var t = At(theme);
            if (landscape) t.landscape = items; else t.portrait = items;
            Dirty = true;
        }

        public static int Add(string name, int baseTheme, List<Widget> land, List<Widget> port)
        {
            var l = new List<CustomTheme>(themes);
            l.Add(new CustomTheme { name = UniqueName(name), baseTheme = baseTheme, landscape = land, portrait = port });
            themes = l; Dirty = true;
            return First + l.Count - 1;
        }

        public static void Remove(int theme)
        {
            var l = new List<CustomTheme>(themes); int i = theme - First;
            if (i <= 0 || i >= l.Count) return; // o primeiro tema proprio sempre existe
            l.RemoveAt(i); themes = l; Dirty = true;
        }

        public static void Rename(int theme, string name)
        {
            name = (name ?? "").Trim();
            if (name == "") return;
            At(theme).name = name; Dirty = true;
        }

        static Widget W(string t, int x, int y, int w, int h, string src, bool bg)
        {
            return new Widget { Type = t, X = x, Y = y, W = w, H = h, Source = src, Bg = bg };
        }

        public static List<Widget> Default(bool land)
        {
            if (land) return new List<Widget> {
                W("clock", 10, 6, 210, 62, "", false), W("date", 230, 24, 240, 26, "", false),
                W("metric", 10, 78, 110, 72, "cpu", true), W("metric", 130, 78, 110, 72, "gpu", true),
                W("metric", 250, 78, 110, 72, "cputemp", true), W("metric", 370, 78, 100, 72, "gputemp", true),
                W("graph", 10, 160, 225, 150, "cpu", true), W("graph", 245, 160, 225, 150, "gpu", true) };
            return new List<Widget> {
                W("clock", 10, 8, 300, 70, "", false), W("date", 10, 80, 300, 26, "", false),
                W("metric", 10, 116, 145, 76, "cpu", true), W("metric", 165, 116, 145, 76, "gpu", true),
                W("metric", 10, 202, 145, 76, "cputemp", true), W("metric", 165, 202, 145, 76, "gputemp", true),
                W("graph", 10, 288, 300, 84, "cpu", true), W("graph", 10, 382, 300, 84, "gpu", true) };
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var f = new JavaScriptSerializer().Deserialize<LayoutFile>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (f == null) return;
                var l = new List<CustomTheme>();
                if (f.themes != null)
                    foreach (var t in f.themes)
                    {
                        if (t == null) continue;
                        t.name = string.IsNullOrWhiteSpace(t.name) ? "Tema" : t.name;
                        t.landscape = Clean(t.landscape); t.portrait = Clean(t.portrait);
                        l.Add(t);
                    }
                if (l.Count == 0) l.Add(new CustomTheme { landscape = Clean(f.landscape), portrait = Clean(f.portrait) });
                themes = l;
            }
            catch (Exception ex)
            {
                Log.Error("layout", ex);
                try { File.Copy(FilePath, FilePath + ".invalido", true); } catch { }
            }
        }

        static List<Widget> Clean(List<Widget> l)
        {
            if (l == null) return null;
            var r = new List<Widget>();
            foreach (var w in l)
            {
                if (w == null) continue;
                w.W = Math.Max(16, Math.Min(480, w.W)); w.H = Math.Max(16, Math.Min(480, w.H));
                w.X = Math.Max(0, Math.Min(480, w.X)); w.Y = Math.Max(0, Math.Min(480, w.Y));
                w.Font = Math.Max(0, Math.Min(200, w.Font)); w.Text = w.Text ?? ""; w.Color = w.Color ?? ""; w.Path = w.Path ?? ""; w.Source = w.Source ?? "cpu"; w.Type = w.Type ?? "text";
                r.Add(w);
            }
            return r;
        }

        public static void Save()
        {
            if (!Dirty) return;
            Dirty = false;
            try
            {
                var js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                string text = js.Serialize(new LayoutFile { themes = themes }), tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                ReplaceFile(tmp, FilePath);
            }
            catch (Exception ex) { Dirty = true; Log.Error("layout", ex); }
        }

        // antivirus e indexador seguram o arquivo por instantes: tenta de novo antes de desistir
        public static void ReplaceFile(string tmp, string path)
        {
            for (int i = 0; ; i++)
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                    return;
                }
                catch (IOException) { if (i >= 4) throw; System.Threading.Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { if (i >= 4) throw; System.Threading.Thread.Sleep(100); }
        }
    }

    partial class MainForm
    {
        readonly object widgetLock = new object();
        readonly Dictionary<string, Bitmap> widgetImages = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        EditorForm editor;
        volatile bool editorOpen;
        volatile int editTheme = LayoutStore.First;

        void DrawCustom(Graphics g, int w, int h, int theme)
        {
            DrawBackground(g, w, h);
            foreach (var x in LayoutStore.Get(theme, w > h)) DrawWidget(g, x);
        }

        CardData WidgetCard(string src, bool detail)
        {
            if (src == "fps")
                return new CardData { Label = "FPS", Value = Gaming.Fps >= 0 ? Gaming.Fps.ToString("0") : "—", Sub = Gaming.Name };
            if (src == "rampct")
                return new CardData { Label = "RAM", Value = SysInfo.RamPct.ToString("0") + "%", Pct = SysInfo.RamPct };
            if (src == "ramgb")
                return new CardData { Label = "RAM", Value = SysInfo.RamUsedGb.ToString("0.0", PT) + " GB", Pct = SysInfo.RamPct };
            try { return BuildCard(src, detail); } catch { return null; }
        }

        static string CardFallbackLabel(string src)
        {
            int i = Array.IndexOf(CardIds, src);
            return i >= 0 ? CardNames[i] : src.ToUpperInvariant();
        }

        // cores que acompanham a paleta: "" texto, "accent" destaque, "dim" apagado, "temp" conforme a temperatura
        Color WidgetColor(string c)
        {
            if (c == "accent") return accent;
            if (c == "dim") return Color.FromArgb(170, fgColor);
            if (c == "" || c == "temp") return fgColor;
            try { return ColorTranslator.FromHtml(c); } catch { return fgColor; }
        }

        // {cpu}, {ram}... no widget de texto
        string ExpandText(string s)
        {
            if (s.IndexOf('{') < 0) return s;
            return s.Replace("{cpu}", SysInfo.Cpu.ToString("0")).Replace("{ramgb}", SysInfo.RamUsedGb.ToString("0.0", PT)).Replace("{ram}", SysInfo.RamPct.ToString("0"))
                .Replace("{gputemp}", SysInfo.GpuTemp >= 0 ? SysInfo.GpuTemp.ToString("0") : "—").Replace("{gpu}", SysInfo.GpuPct >= 0 ? SysInfo.GpuPct.ToString("0") : "—")
                .Replace("{cputemp}", CpuTemp.Value >= 0 ? CpuTemp.Value.ToString("0") : "—")
                .Replace("{vpn}", VpnText()).Replace("{gpufan}", Lhm.GpuFan >= 0 ? Lhm.GpuFan.ToString("0") : "—").Replace("{fan}", Lhm.CaseFan >= 0 ? Lhm.CaseFan.ToString("0") : "—")
                .Replace("{down}", SysInfo.Rate(SysInfo.NetDown)).Replace("{up}", SysInfo.Rate(SysInfo.NetUp))
                .Replace("{ss}", DateTime.Now.ToString("ss")).Replace("{title}", title)
                .Replace("{game}", Gaming.Name != "" ? Gaming.Name : "Nenhum jogo aberto")
                .Replace("{place}", Widgets.Place).Replace("{temp}", Widgets.HasWeather ? Widgets.Temp.ToString("0") : "—");
        }

        // desenvolvimento: tema original (esquerda) e copia em widgets (direita), sem abrir janelas
        void ThemeShots(string dir)
        {
            var t = new System.Windows.Forms.Timer(); t.Interval = 12000; t.Start(); // espera a primeira leitura das baterias
            t.Tick += delegate
            {
                t.Stop();
                foreach (bool land in new[] { true, false })
                    for (int theme = 0; theme < LayoutStore.First; theme++)
                    {
                        int w = land ? 480 : 320, h = land ? 320 : 480;
                        using (var b = new Bitmap(w * 2 + 8, h, PixelFormat.Format24bppRgb)) using (var g = Graphics.FromImage(b))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.Magenta); g.SetClip(new Rectangle(0, 0, w, h));
                            DrawDashboard(g, w, h, theme);
                            g.ResetClip(); g.TranslateTransform(w + 8, 0); g.SetClip(new Rectangle(0, 0, w, h));
                            DrawBackground(g, w, h);
                            foreach (var x in ThemeToWidgets(theme, land)) DrawWidget(g, x);
                            g.ResetTransform(); g.ResetClip();
                            b.Save(System.IO.Path.Combine(dir, "t" + theme + (land ? "L" : "P") + ".png"));
                        }
                    }
                Close();
            };
        }

        // ------------------------------------------------------------ tema pronto -> widgets
        static Widget WText(string text, int x, int y, int w, int h, int font, string color, int align)
        {
            return new Widget { Type = "text", Text = text, X = x, Y = y, W = w, H = h, Font = font, Color = color, Align = align, Bg = false };
        }

        static Widget WData(string type, string src, int x, int y, int w, int h, int font, bool label, bool bar, bool bg)
        {
            return new Widget { Type = type, Source = src, X = x, Y = y, W = w, H = h, Font = font, Label = label, Bar = bar, Bg = bg };
        }

        static Widget WBox(string type, Rectangle r, bool bg)
        {
            return new Widget { Type = type, Source = "", X = r.X, Y = r.Y, W = r.Width, H = r.Height, Bg = bg };
        }

        static void AddHeader(List<Widget> l, int w)
        {
            int tw = w > 400 ? 280 : 200;
            l.Add(new Widget { Type = "clock", X = 4, Y = 0, W = 190, H = 50, Font = 34, Seconds = true, Bold = true, Bg = false });
            l.Add(new Widget { Type = "date", X = w - tw, Y = 6, W = tw - 6, H = 24, Font = 14, Align = 2, Color = "dim", Bg = false });
            l.Add(WText("{title}", w - tw, 25, tw - 6, 25, 14, "accent", 2));
        }

        public List<Widget> ThemeToWidgets(int theme, bool land)
        {
            int w = land ? 480 : 320, h = land ? 320 : 480;
            var l = new List<Widget>();
            string net = "↓ {down}    ↑ {up}";
            switch (theme)
            {
                case 1: // graficos
                {
                    AddHeader(l, w);
                    var ids = new List<string>();
                    foreach (var id in cardOrder)
                        if (cardOn.Contains(id) && (id == "cpu" || id == "ram" || (id == "gpu" && SysInfo.GpuPct >= 0))) ids.Add(id);
                    if (ids.Count == 0) ids.Add("cpu");
                    int top = 52, pad = 8, rh = (h - top - pad * (ids.Count + 1) - (showNet ? 22 : 0)) / ids.Count, labelW = land ? 96 : 84;
                    for (int i = 0; i < ids.Count; i++)
                    {
                        string id = ids[i];
                        var r = new Rectangle(pad, top + pad + i * (rh + pad), w - pad * 2, rh);
                        l.Add(WBox("panel", r, true));
                        l.Add(WText(id.ToUpperInvariant(), r.X + 2, r.Y + 2, labelW, 24, 13, "accent", 0));
                        l.Add(WText(id == "cpu" ? "{cpu}%" : id == "ram" ? "{ram}%" : "{gpu}%", r.X, r.Y + 20, labelW + 8, 38, 26, "", 0));
                        l.Add(WText(id == "cpu" ? "{cputemp} °C" : id == "ram" ? "{ramgb} GB" : "{gputemp} °C", r.X + 2, r.Y + 52, labelW + 4, 24, 12, "dim", 0));
                        l.Add(WData("graph", id, r.X + labelW - 7, r.Y + 1, r.Width - labelW + 6, r.Height - 2, 0, false, true, false));
                    }
                    if (showNet) l.Add(WText(net, pad - 2, h - 25, w - pad * 2, 24, 12, "dim", 0));
                    break;
                }
                case 2: // relogio grande
                {
                    int big = land ? 132 : 104;
                    float hmW, hmH, sW, sH;
                    using (var b = new Bitmap(4, 4)) using (var g = Graphics.FromImage(b))
                    using (var fBig = SF.New("Segoe UI Light", big)) using (var fSec = SF.New("Segoe UI Light", 40))
                    using (var sf = new StringFormat(StringFormat.GenericTypographic))
                    {
                        var a = g.MeasureString(TimeStr(false), fBig, 1000, sf); var c = g.MeasureString("00", fSec, 1000, sf);
                        hmW = a.Width; hmH = a.Height; sW = c.Width; sH = c.Height;
                    }
                    int x = (int)((w - (hmW + 8 + sW)) / 2), y = (int)(h / 2 - hmH / 2 - 30);
                    // o DrawString comum reserva cerca de 1/6 da fonte de cada lado
                    l.Add(new Widget { Type = "clock", X = x - big / 6 - 6, Y = y, W = (int)hmW + big / 3 + 28, H = (int)hmH, Font = big, Bg = false });
                    var sec = WText("{ss}", x + (int)hmW + 1, y + (int)(hmH - sH) - 18, (int)sW + 44, (int)sH + 8, 40, "accent", 0);
                    sec.Thin = true; l.Add(sec);
                    l.Add(new Widget { Type = "date", X = 0, Y = y + (int)hmH, W = w, H = 34, Font = 20, Align = 1, Color = "dim", Bg = false });
                    string stats = "CPU {cpu}%   RAM {ram}%";
                    if (showGpu && SysInfo.GpuPct >= 0) stats += "   GPU {gpu}% {gputemp}°C";
                    if (Widgets.HasWeather) stats = "{place} {temp}°   ·   " + stats;
                    l.Add(WText(stats, 0, h - 30, w, 26, 14, "accent", 1));
                    break;
                }
                case 3: // medidores
                {
                    AddHeader(l, w);
                    var ids = new List<string>();
                    foreach (var id in cardOrder)
                    {
                        if (ids.Count >= 3 || !cardOn.Contains(id)) continue;
                        var c = BuildCard(id, false);
                        if (c != null && c.Pct >= 0) ids.Add(id == "ram" ? "rampct" : id);
                    }
                    if (ids.Count == 0) ids.Add("cpu");
                    int top = 52, cols = land ? ids.Count : 1, rows = (ids.Count + cols - 1) / cols, bottom = showNet ? h - 26 : h - 6;
                    int cellW = w / cols, cellH = (bottom - top) / rows, s = Math.Min(cellW, cellH) - 6;
                    for (int i = 0; i < ids.Count; i++)
                        l.Add(WData("gauge", ids[i], (i % cols) * cellW + (cellW - s) / 2, top + (i / cols) * cellH + (cellH - s) / 2, s, s, 26, true, true, false));
                    if (showNet) l.Add(WText(net, 0, h - 25, w, 24, 13, "dim", 1));
                    break;
                }
                case 4: // clima e cotacoes
                {
                    AddHeader(l, w);
                    int top = 52;
                    l.Add(WBox("forecast", land ? new Rectangle(8, top + 6, w / 2 - 12, h - top - 14) : new Rectangle(8, top + 6, w - 16, (h - top) / 2 - 10), true));
                    l.Add(WBox("quotes", land ? new Rectangle(w / 2 + 4, top + 6, w / 2 - 12, h - top - 14) : new Rectangle(8, top + (h - top) / 2 + 2, w - 16, (h - top) / 2 - 10), true));
                    break;
                }
                case 5: // tocando agora
                    l.Add(WBox("player", new Rectangle(0, 0, w, h), false));
                    l.Add(new Widget { Type = "clock", X = w - 130, Y = land ? h - 46 : 0, W = 124, H = 46, Font = 30, Align = 2, Color = "#FFFFFF", Bg = false });
                    break;
                case 6: // gaming
                {
                    l.Add(WText("{game}", 4, 3, w - 86, 30, 16, "accent", 0));
                    l.Add(new Widget { Type = "clock", X = w - 84, Y = 3, W = 80, H = 30, Font = 16, Align = 2, Bold = true, Bg = false });
                    int top = 34, pad = 8;
                    var fr = land ? new Rectangle(pad, top, 190, 132) : new Rectangle(pad, top, w - pad * 2, 118);
                    var tr = land ? new Rectangle(fr.Right + pad, top, w - fr.Right - pad * 2, 132) : new Rectangle(pad, fr.Bottom + pad, w - pad * 2, 150);
                    l.Add(WBox("fps", fr, true));
                    string[] src = { "gpu", "gputemp", "cpu", "cputemp", "ramgb", "gpuclock" };
                    int gap = 6, tw = (tr.Width - gap) / 2, th = (tr.Height - gap * 2) / 3;
                    for (int i = 0; i < src.Length; i++)
                    {
                        var m = WData("metric", src[i], tr.X + (i % 2) * (tw + gap), tr.Y + (i / 2) * (th + gap), tw, th, 20, true, false, true);
                        if (src[i].EndsWith("temp")) m.Color = "temp";
                        l.Add(m);
                    }
                    int gy = tr.Bottom + pad, gh = h - gy - pad;
                    if (gh >= 34) l.Add(WData("graph", "fps", pad, gy, w - pad * 2, gh, 0, true, true, true));
                    break;
                }
                case 7: // agenda
                    AddHeader(l, w);
                    l.Add(WBox("agenda", new Rectangle(8, 56, w - 16, h - 64), false));
                    break;
                case 8: // temperaturas
                {
                    AddHeader(l, w);
                    var ids = new List<string>();
                    foreach (var id in TempIds) { var c = BuildCard(id, false); if (c != null && c.Pct >= 0) ids.Add(id); }
                    if (ids.Count == 0) ids.Add("cputemp");
                    string foot = TempFooter() != null ? (Lhm.CaseFan >= 0 ? "Ventoinhas {fan} RPM" : "") + (Lhm.CaseFan >= 0 && Lhm.GpuFan >= 0 ? "   ·   " : "") + (Lhm.GpuFan >= 0 ? "Fan GPU {gpufan} RPM" : "") : null;
                    int top = 52, cols = land ? ids.Count : 1, rows = (ids.Count + cols - 1) / cols, bottom = foot != null ? h - 26 : h - 6;
                    int cellW = w / cols, cellH = (bottom - top) / rows, s = Math.Min(cellW, cellH) - 6;
                    for (int i = 0; i < ids.Count; i++)
                        l.Add(WData("gauge", ids[i], (i % cols) * cellW + (cellW - s) / 2, top + (i / cols) * cellH + (cellH - s) / 2, s, s, 26, true, true, false));
                    if (foot != null) l.Add(WText(foot, 0, h - 25, w, 24, 13, "dim", 1));
                    break;
                }
                case 9: // baterias
                    AddHeader(l, w);
                    l.Add(new Widget { Type = "battery", Source = "all", X = 8, Y = 56, W = w - 16, H = h - 64, Label = false, Bg = true });
                    break;
                case VpnTheme:
                    AddHeader(l, w);
                    l.Add(new Widget { Type = "vpn", Source = "", X = 8, Y = 58, W = w - 16, H = Math.Min(h - 66, 120), Label = false, Bg = true });
                    break;
                default: // cartoes
                {
                    l.Add(new Widget { Type = "clock", X = 8, Y = 2, W = 240, H = 66, Font = land ? 50 : 54, Seconds = true, Bg = false });
                    l.Add(new Widget { Type = "date", X = 12, Y = 62, W = land ? 270 : 296, H = 26, Font = 15, Color = "dim", Bg = false });
                    if (land) l.Add(WText("{title}", w - 196, 62, 188, 26, 15, "accent", 2));
                    else l.Add(WText("{title}", 12, 82, 296, 26, 15, "accent", 0));
                    int top = land ? 98 : 116;
                    bool inCards = specMode == 1 || specMode == 3;
                    if (specMode == 2 || specMode == 3)
                    {
                        int sx = land ? 246 : 11, sy = land ? 10 : 104, sw = land ? 227 : 298;
                        foreach (var id in new[] { "cpu", "gpuram" })
                        {
                            var s = WData("specs", id, sx, sy, sw, 26, 13, false, true, false);
                            s.Align = land ? 2 : 0; s.Color = "dim"; l.Add(s); sy += 18;
                        }
                        if (!land) top = 146;
                    }
                    var cards = new List<KeyValuePair<string, CardData>>();
                    foreach (var id in cardOrder)
                        if (cardOn.Contains(id)) { var c = BuildCard(id, inCards); if (c != null) cards.Add(new KeyValuePair<string, CardData>(id, c)); }
                    int cols = land ? 3 : 2, pad = 10, rows = (cards.Count + cols - 1) / cols;
                    if (rows == 0) break;
                    int cw = (w - pad * (cols + 1)) / cols, ch = Math.Min(110, (h - top - pad * (rows + 1)) / rows);
                    bool compact = ch < 80;
                    for (int i = 0; i < cards.Count; i++)
                    {
                        var m = WData("metric", cards[i].Key, pad + (i % cols) * (cw + pad), top + pad + (i / cols) * (ch + pad), cw, ch, compact ? 20 : 24, true, true, true);
                        m.Detail = !compact && ch >= 84 && cards[i].Value.Sub != "";
                        l.Add(m);
                    }
                    break;
                }
            }
            foreach (var x in l)
            {
                x.W = Math.Max(16, Math.Min(w, x.W)); x.H = Math.Max(16, Math.Min(h, x.H));
                x.X = Math.Max(0, Math.Min(w - x.W, x.X)); x.Y = Math.Max(0, Math.Min(h - x.H, x.Y));
            }
            return l;
        }

        static Font FitFont(Graphics g, string s, string family, int maxW, int maxH, int fixedPx)
        {
            if (fixedPx > 0) return SF.New(family, fixedPx);
            int lo = 8, hi = Math.Max(9, Math.Min(160, maxH));
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2; bool fits;
                using (var f = SF.New(family, mid))
                    // mede como o DrawString desenha (com as margens dele), senao o texto vira "..."
                    fits = g.MeasureString(s == "" ? " " : s, f).Width <= maxW && mid * 1.2 <= maxH;
                if (fits) lo = mid; else hi = mid - 1;
            }
            return SF.New(family, lo);
        }

        void DrawWidget(Graphics g, Widget x)
        {
            var r = x.Rect;
            if (r.Width < 4 || r.Height < 4) return;
            Color fore = WidgetColor(x.Color);
            var state = g.Save();
            try
            {
                if (x.Bg)
                    using (var p = Round(r, Math.Min(8, Math.Min(r.Width, r.Height) / 3)))
                    using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillPath(b, p);
                g.SetClip(r);
                using (var fg = new SolidBrush(fore)) using (var dim = new SolidBrush(Color.FromArgb(170, fore)))
                using (var acc = new SolidBrush(accent))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = x.Align == 1 ? StringAlignment.Center : x.Align == 2 ? StringAlignment.Far : StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap;
                    var inner = new Rectangle(r.X + 6, r.Y + 3, Math.Max(1, r.Width - 12), Math.Max(1, r.Height - 6));
                    switch (x.Type)
                    {
                        case "clock": DrawFit(g, TimeStr(x.Seconds), x.Bold ? "Segoe UI Semibold" : "Segoe UI Light", inner, x.Font, fg, sf); break;
                        case "date": DrawFit(g, DateStr(), "Segoe UI", inner, x.Font, fg, sf); break;
                        case "text": DrawWidgetText(g, x, inner, fg, sf); break;
                        case "metric": DrawWidgetMetric(g, x, r, fg, acc, sf); break;
                        case "gauge": DrawWidgetGauge(g, x, r, fg, acc, sf); break;
                        case "graph": DrawWidgetGraph(g, x, r, fg, acc, sf); break;
                        case "weather": DrawWidgetWeather(g, x, r, fg, dim, sf); break;
                        case "music": DrawWidgetMusic(g, x, r, fg, dim, sf); break;
                        case "image": DrawWidgetImage(g, x, r, dim, sf); break;
                        case "specs": DrawWidgetSpecs(g, x, r, fg, acc, sf); break;
                        case "battery": DrawWidgetBattery(g, x, r, fg, acc, sf); break;
                        case "vpn": DrawVpnBox(g, new Rectangle(r.X + 2, r.Y + 2, Math.Max(1, r.Width - 4), Math.Max(1, r.Height - 4)), false, x.Label, x.Font); break;
                        case "forecast": g.TranslateTransform(r.X, r.Y); DrawWeatherBox(g, new Rectangle(0, 0, r.Width, r.Height)); break;
                        case "quotes": g.TranslateTransform(r.X, r.Y); DrawQuotesBox(g, new Rectangle(0, 0, r.Width, r.Height)); break;
                        case "agenda": g.TranslateTransform(r.X, r.Y); DrawAgendaBox(g, new Rectangle(0, 0, r.Width, r.Height)); break;
                        case "fps": g.TranslateTransform(r.X, r.Y); DrawFpsBox(g, new Rectangle(0, 0, r.Width, r.Height)); break;
                        case "player":
                            if (!Media.Has) { DrawFit(g, "Nada tocando", "Segoe UI", inner, x.Font > 0 ? x.Font : 16, dim, sf); break; }
                            g.TranslateTransform(r.X, r.Y);
                            lock (widgetLock) DrawPlayer(g, r.Width, r.Height);
                            break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("widget", ex); }
            finally { g.Restore(state); }
        }

        static void DrawFit(Graphics g, string s, string family, Rectangle r, int px, Brush b, StringFormat sf)
        {
            using (var f = FitFont(g, s, family, r.Width, r.Height, px)) g.DrawString(s, f, b, r, sf);
        }

        void DrawWidgetText(Graphics g, Widget x, Rectangle r, Brush fg, StringFormat sf)
        {
            string text = ExpandText(x.Text.Replace("\r", ""));
            var lines = text.Split('\n');
            string longest = ""; foreach (var l in lines) if (l.Length > longest.Length) longest = l;
            using (var f = FitFont(g, longest, x.Thin ? "Segoe UI Light" : "Segoe UI Semibold", r.Width, r.Height / Math.Max(1, lines.Length), x.Font))
            using (var m = (StringFormat)sf.Clone())
            {
                m.FormatFlags = 0;
                g.DrawString(text, f, fg, r, m);
            }
        }

        void DrawWidgetMetric(Graphics g, Widget x, Rectangle r, Brush fg, Brush acc, StringFormat sf)
        {
            var c = WidgetCard(x.Source, x.Detail) ?? new CardData { Label = CardFallbackLabel(x.Source), Value = "—" };
            int top = r.Y + 4, bottom = r.Bottom - 4;
            if (x.Label)
            {
                using (var f = SF.New("Segoe UI Semibold", Math.Max(9, Math.Min(13, r.Height / 5))))
                    g.DrawString(c.Label, f, acc, new RectangleF(r.X + 7, top, r.Width - 12, f.Height), sf);
                top += Math.Max(11, Math.Min(16, r.Height / 4));
            }
            if (x.Bar && c.Pct >= 0 && r.Height >= 44)
            {
                var bar = new Rectangle(r.X + 7, r.Bottom - 10, r.Width - 14, 5);
                using (var bb = new SolidBrush(Color.FromArgb(50, fgColor))) g.FillRectangle(bb, bar);
                bar.Width = (int)(bar.Width * Math.Min(100, c.Pct) / 100);
                using (var b = new SolidBrush(CardColor(c, c.Pct))) if (bar.Width > 0) g.FillRectangle(b, bar);
                bottom -= 9;
            }
            if (x.Detail && c.Sub != "" && bottom - top >= 40)
            {
                bottom -= 16;
                using (var f = SF.New("Segoe UI", 12)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
                    g.DrawString(c.Sub, f, dim, new RectangleF(r.X + 7, bottom, r.Width - 12, 16), sf);
            }
            var valueRect = new Rectangle(r.X + 7, top, Math.Max(1, r.Width - 12), Math.Max(1, bottom - top));
            if (x.Color == "temp" && (c.Temp || c.Bat) && c.Pct >= 0)
                using (var tb = new SolidBrush(CardColor(c, c.Pct))) DrawFit(g, c.Value, "Segoe UI Semibold", valueRect, x.Font, tb, sf);
            else DrawFit(g, c.Value, "Segoe UI Semibold", valueRect, x.Font, fg, sf);
        }

        void DrawWidgetGauge(Graphics g, Widget x, Rectangle r, Brush fg, Brush acc, StringFormat sf)
        {
            var c = WidgetCard(x.Source, false) ?? new CardData { Label = CardFallbackLabel(x.Source), Value = "—" };
            int th = Math.Max(4, Math.Min(r.Width, r.Height) / 9), d = Math.Min(r.Width, r.Height) - th - 8;
            var a = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            double v = Math.Max(0, Math.Min(100, c.Pct));
            using (var bp = new Pen(Color.FromArgb(60, fgColor), th)) g.DrawArc(bp, a, 135, 270);
            using (var p = new Pen(CardColor(c, v), th))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (v > 0.5) g.DrawArc(p, a, 135, (float)(270 * v / 100));
            }
            using (var m = (StringFormat)sf.Clone())
            {
                m.Alignment = StringAlignment.Center;
                DrawFit(g, c.Value, "Segoe UI Semibold", new Rectangle(a.X + th, a.Y + d / 4, Math.Max(1, d - th * 2), Math.Max(1, d / 2)), x.Font, fg, m);
                if (x.Label) using (var f = SF.New("Segoe UI", Math.Max(9, d / 8)))
                    g.DrawString(c.Label, f, acc, new RectangleF(r.X, a.Bottom - f.Height - 1, r.Width, f.Height + 2), m);
            }
        }

        void DrawWidgetGraph(Graphics g, Widget x, Rectangle r, Brush fg, Brush acc, StringFormat sf)
        {
            float[] hist = SysInfo.HCpu; int count = SysInfo.HCount;
            if (x.Source == "ram") hist = SysInfo.HRam;
            else if (x.Source == "gpu") hist = SysInfo.HGpu;
            else if (x.Source == "fps") { hist = Gaming.Hist; count = Gaming.HCount; }
            int top = r.Y + 5;
            if (x.Label)
            {
                var c = WidgetCard(x.Source, false);
                using (var f = SF.New("Segoe UI Semibold", 12)) using (var m = (StringFormat)sf.Clone())
                {
                    g.DrawString(c != null ? c.Label : x.Source, f, acc, new RectangleF(r.X + 7, top, r.Width / 2, 16), sf);
                    m.Alignment = StringAlignment.Far;
                    g.DrawString(c != null ? c.Value : "—", f, fg, new RectangleF(r.X + r.Width / 2, top, r.Width / 2 - 7, 16), m);
                }
                top += 18;
            }
            DrawSweep(g, new Rectangle(r.X + 7, top, Math.Max(3, r.Width - 14), Math.Max(3, r.Bottom - top - 6)), hist, acc, count);
        }

        void DrawWidgetWeather(Graphics g, Widget x, Rectangle r, Brush fg, Brush dim, StringFormat sf)
        {
            if (!Widgets.HasWeather) { DrawFit(g, "Sem clima", "Segoe UI", new Rectangle(r.X + 6, r.Y + 4, r.Width - 12, Math.Min(22, r.Height - 8)), 0, dim, sf); return; }
            int s = Math.Min(r.Height - 8, r.Width / 2);
            Widgets.DrawIcon(g, Widgets.Code, Widgets.IsDay, new RectangleF(r.X + 4, r.Y + (r.Height - s) / 2, s, s), accent, fgColor);
            int tx = r.X + s + 8, tw = Math.Max(1, r.Right - tx - 5), th = r.Height >= 54 ? r.Height * 3 / 5 : r.Height - 6;
            DrawFit(g, Widgets.Temp.ToString("0") + "°", "Segoe UI Light", new Rectangle(tx, r.Y + 3, tw, Math.Max(1, th)), x.Font, fg, sf);
            if (r.Height >= 54) DrawFit(g, Widgets.Describe(Widgets.Code), "Segoe UI", new Rectangle(tx, r.Y + 3 + th, tw, Math.Min(18, r.Bottom - r.Y - th - 6)), 0, dim, sf);
        }

        void DrawWidgetMusic(Graphics g, Widget x, Rectangle r, Brush fg, Brush dim, StringFormat sf)
        {
            if (!Media.Has) { DrawFit(g, "Nada tocando", "Segoe UI", new Rectangle(r.X + 7, r.Y + 4, r.Width - 12, Math.Min(22, r.Height - 8)), 0, dim, sf); return; }
            int s = r.Height - 8, tx = r.X + 6;
            var cover = Media.Cover;
            if (cover != null && s < r.Width / 2)
            {
                lock (widgetLock) g.DrawImage(cover, r.X + 4, r.Y + 4, s, s);
                tx = r.X + s + 10;
            }
            int tw = Math.Max(1, r.Right - tx - 5), h1 = r.Height * 5 / 9;
            using (var f = SF.New("Segoe UI Semibold", x.Font > 0 ? x.Font : Math.Max(10, Math.Min(18, h1 * 2 / 3))))
                g.DrawString(Media.Title, f, fg, new RectangleF(tx, r.Y + 3, tw, h1), sf);
            using (var f = SF.New("Segoe UI", Math.Max(9, Math.Min(14, r.Height / 4))))
                g.DrawString(Media.Artist, f, dim, new RectangleF(tx, r.Y + 3 + h1, tw, r.Height - h1 - 6), sf);
        }

        // uma linha por periferico: tipo, nome, barra e porcentagem
        void DrawWidgetBattery(Graphics g, Widget x, Rectangle r, Brush fg, Brush acc, StringFormat sf)
        {
            var inner = new Rectangle(r.X + 7, r.Y + 4, Math.Max(1, r.Width - 14), Math.Max(1, r.Height - 8));
            var list = new List<BatteryDevice>();
            foreach (var d in Batteries.List) if (x.Source == "all" || x.Source == "" || d.Kind == x.Source) list.Add(d);
            if (x.Label && inner.Height >= 40)
                using (var f = SF.New("Segoe UI Semibold", Math.Max(9, Math.Min(13, inner.Height / 6))))
                using (var left = (StringFormat)sf.Clone())
                {
                    left.Alignment = StringAlignment.Near;
                    g.DrawString("BATERIAS", f, acc, new RectangleF(inner.X, inner.Y, inner.Width, f.Height), left);
                    inner.Y += f.Height + 2; inner.Height -= f.Height + 2;
                }
            using (var dim = new SolidBrush(Color.FromArgb(150, fgColor)))
            {
                if (list.Count == 0) { DrawFit(g, "Nenhum periférico sem fio", "Segoe UI", inner, x.Font > 0 ? x.Font : 14, dim, sf); return; }
                int minRow = x.Font > 0 ? Math.Max(16, (int)(x.Font * 1.5)) : 18;
                int n = Math.Max(1, Math.Min(list.Count, inner.Height / minRow)), rowH = Math.Min(inner.Height / n, 44);
                int px = x.Font > 0 ? x.Font : Math.Max(10, Math.Min(18, rowH * 9 / 20));
                using (var f = SF.New("Segoe UI Semibold", px))
                using (var fs = SF.New("Segoe UI", Math.Max(9, px - 2)))
                using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var barBg = new SolidBrush(Color.FromArgb(50, fgColor)))
                {
                    int pctW = (int)g.MeasureString("100%", f).Width + 4;
                    for (int i = 0; i < n; i++)
                    {
                        var d = list[i];
                        int y = inner.Y + i * rowH;
                        bool bar = rowH >= px + 10;
                        int textH = bar ? rowH - 8 : rowH;
                        string pct = d.Percent >= 0 ? d.Percent + "%" : d.Charging ? "⚡" : "—";
                        Brush main = d.Asleep ? dim : fg;
                        g.DrawString(pct, f, main, new RectangleF(inner.Right - pctW, y, pctW, textH), right);
                        string name = Batteries.KindName(d.Kind) + (d.Charging && d.Percent >= 0 ? " ⚡" : "");
                        float kw = g.MeasureString(name, f).Width;
                        g.DrawString(name, f, main, new RectangleF(inner.X, y, inner.Width - pctW, textH), left);
                        g.DrawString(d.Name + (d.Asleep ? " · dormindo" : ""), fs, dim, new RectangleF(inner.X + kw + 2, y, Math.Max(1, inner.Width - pctW - kw - 2), textH), left);
                        if (bar && d.Percent >= 0)
                        {
                            var b = new Rectangle(inner.X, y + rowH - 7, inner.Width, 4);
                            g.FillRectangle(barBg, b);
                            b.Width = (int)(b.Width * Math.Min(100, d.Percent) / 100.0);
                            using (var bb = new SolidBrush(d.Asleep ? Color.FromArgb(120, BatColor(d.Percent)) : BatColor(d.Percent))) if (b.Width > 0) g.FillRectangle(bb, b);
                        }
                    }
                }
            }
        }

        void DrawWidgetSpecs(Graphics g, Widget x, Rectangle r, Brush fg, Brush acc, StringFormat sf)
        {
            var inner = new Rectangle(r.X + 7, r.Y + 4, Math.Max(1, r.Width - 14), Math.Max(1, r.Height - 8));
            if (!Specs.Loaded) { DrawFit(g, "Detectando...", "Segoe UI", inner, x.Font, fg, sf); return; }
            using (var left = (StringFormat)sf.Clone())
            {
                left.Alignment = StringAlignment.Near;
                var item = Specs.Find(x.Source);
                if (item != null)
                {
                    int top = inner.Y;
                    if (x.Label && inner.Height >= 30)
                        using (var f = SF.New("Segoe UI Semibold", Math.Max(9, Math.Min(13, inner.Height / 4))))
                        {
                            g.DrawString(item[2], f, acc, new RectangleF(inner.X, top, inner.Width, f.Height), left);
                            top += f.Height;
                        }
                    string v = Specs.Value(item[0]);
                    DrawFit(g, v != "" ? v : "Não detectado", "Segoe UI Semibold", new Rectangle(inner.X, top, inner.Width, Math.Max(1, inner.Bottom - top)), x.Font, fg, sf);
                    return;
                }

                // resumo: uma linha por dado, ate onde couber na altura do widget
                var rows = new List<string[]>();
                foreach (var i in Specs.Items) { string v = Specs.Value(i[0]); if (v != "") rows.Add(new[] { i[1], v }); }
                if (rows.Count == 0) { DrawFit(g, "Não detectado", "Segoe UI", inner, x.Font, fg, sf); return; }
                int minRow = x.Font > 0 ? Math.Max(12, (int)(x.Font * 1.2)) : 12;
                int n = Math.Max(1, Math.Min(rows.Count, inner.Height / minRow)), rowH = inner.Height / n;
                using (var lf = SF.New("Segoe UI Semibold", x.Font > 0 ? x.Font : Math.Max(9, Math.Min(13, rowH * 2 / 3))))
                {
                    int labelW = 0; string longest = "";
                    for (int i = 0; i < n; i++)
                    {
                        if (x.Label) labelW = Math.Max(labelW, (int)Math.Ceiling(g.MeasureString(rows[i][0], lf).Width) + 4);
                        if (rows[i][1].Length > longest.Length) longest = rows[i][1];
                    }
                    int vw = Math.Max(1, inner.Width - labelW);
                    using (var vf = FitFont(g, longest, "Segoe UI", vw, Math.Min(rowH, 22), x.Font))
                        for (int i = 0; i < n; i++)
                        {
                            int y = inner.Y + i * rowH;
                            if (x.Label) g.DrawString(rows[i][0], lf, acc, new RectangleF(inner.X, y, labelW, rowH), left);
                            g.DrawString(rows[i][1], vf, fg, new RectangleF(inner.X + labelW, y, vw, rowH), sf);
                        }
                }
            }
        }

        void DrawWidgetImage(Graphics g, Widget x, Rectangle r, Brush dim, StringFormat sf)
        {
            lock (widgetLock)
            {
                Bitmap img = null;
                if (x.Path != "" && !widgetImages.TryGetValue(x.Path, out img))
                {
                    try { using (var ms = new MemoryStream(File.ReadAllBytes(x.Path))) using (var src = Image.FromStream(ms)) img = new Bitmap(src); }
                    catch { img = null; }
                    widgetImages[x.Path] = img;
                }
                if (img == null) { DrawFit(g, "Sem imagem", "Segoe UI", new Rectangle(r.X + 5, r.Y + 3, Math.Max(1, r.Width - 10), Math.Min(20, r.Height - 6)), 0, dim, sf); return; }
                g.TranslateTransform(r.X, r.Y);
                DrawCover(g, img, r.Width, r.Height, x.Fill);
            }
        }

        void OpenEditor()
        {
            if (editor != null && !editor.IsDisposed) { editor.Activate(); return; }
            int theme = Math.Max(0, cboTheme.SelectedIndex), previous = theme;
            bool created = theme < LayoutStore.First;
            if (created)
            {
                // tema pronto nunca e alterado: a edicao acontece numa copia em widgets
                string name = ThemeNames[theme]; int cut = name.IndexOf(" ("); if (cut > 0) name = name.Substring(0, cut);
                theme = LayoutStore.Add(name + " personalizado", theme, ThemeToWidgets(theme, true), ThemeToWidgets(theme, false));
                dashTheme = theme; RefreshThemeLists();
            }
            editTheme = theme; editorOpen = true; lcd.Invalidate();
            var form = new EditorForm(this, theme);
            editor = form;
            form.FormClosed += delegate
            {
                editorOpen = false;
                // copia aberta e fechada sem mudar nada nao fica na lista
                if (form.DeleteRequested || (created && !form.Modified)) RemoveTheme(theme, created ? previous : LayoutStore.First);
                LayoutStore.Save(); lcd.Invalidate();
            };
            form.Show(this);
        }

        void RemoveTheme(int theme, int fallback)
        {
            LayoutStore.Remove(theme);
            dashTheme = dashTheme == theme ? fallback : dashTheme > theme ? dashTheme - 1 : dashTheme;
            var rt = new List<int>();
            foreach (int t in rotateThemes) if (t != theme) rt.Add(t > theme ? t - 1 : t);
            rotateThemes = rt.ToArray();
            RefreshThemeLists();
        }

        // ------------------------------------------------------------ janela
        class CanvasPanel : Panel
        {
            public CanvasPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
                TabStop = true;
            }
        }

        class EditorForm : Form
        {
            static readonly string[][] Types = {
                new[] { "clock", "Relógio" }, new[] { "date", "Data" }, new[] { "text", "Texto" }, new[] { "metric", "Métrica" },
                new[] { "gauge", "Medidor" }, new[] { "graph", "Gráfico" }, new[] { "weather", "Clima" }, new[] { "forecast", "Previsão" },
                new[] { "quotes", "Cotações" }, new[] { "music", "Música" }, new[] { "player", "Player" }, new[] { "agenda", "Agenda" },
                new[] { "fps", "Painel FPS" }, new[] { "specs", "Máquina" }, new[] { "battery", "Baterias" }, new[] { "vpn", "VPN" },new[] { "image", "Imagem" }, new[] { "panel", "Painel" } };
            public bool DeleteRequested;
            public bool Modified { get { return undo.Count > 0 || renamed; } }
            bool renamed;
            readonly int theme;
            readonly MainForm m;
            readonly bool land; readonly int sw, sh; readonly float zoom;
            List<Widget> items;
            Widget sel;
            readonly CanvasPanel canvas = new CanvasPanel();
            readonly Bitmap buffer;
            readonly Stack<string> undo = new Stack<string>();
            readonly JavaScriptSerializer js = new JavaScriptSerializer();
            int dragMode = -1; Point dragStart; Rectangle dragOrig; bool dragPushed; // -1 nada, 0 mover, 1..4 cantos
            bool loading;
            Label lblType, lblSource, lblFont, lblAlign;
            NumericUpDown nX, nY, nW, nH, nFont;
            ComboBox cbSource, cbAlign;
            TextBox txt;
            CheckBox cBg, cLabel, cSec, cFill, cBar, cDetail, cBold, cThin;
            Button bColor, bTheme, bImage;
            Control lblTokens;
            Panel props;

            public EditorForm(MainForm owner, int themeIndex)
            {
                m = owner; theme = themeIndex;
                land = m.orientation >= 2; sw = land ? 480 : 320; sh = land ? 320 : 480;
                zoom = Math.Min(720f / sw, 510f / sh);
                buffer = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
                items = new List<Widget>(LayoutStore.Get(theme, land));
                Text = "Editor de layout"; Font = owner.Font; Icon = owner.Icon;
                StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; KeyPreview = true;
                int cw = (int)(sw * zoom), ch = (int)(sh * zoom);
                ClientSize = new Size(136 + cw + 240, Math.Max(ch + 76, 656));

                int y = 10;
                m.L("Adicionar", 10, y, this); y += 22;
                foreach (var t in Types)
                {
                    string type = t[0];
                    m.Btn(t[1], 10, y, 112, this, delegate { AddWidget(type); }); y += 29;
                }
                y += 8;
                m.Btn("Duplicar", 10, y, 112, this, delegate { Duplicate(); }); y += 29;
                m.Btn("Trazer p/ frente", 10, y, 112, this, delegate { Reorder(true); }); y += 29;
                m.Btn("Enviar p/ trás", 10, y, 112, this, delegate { Reorder(false); }); y += 29;
                m.Btn("Excluir", 10, y, 112, this, delegate { Delete(); }); y += 29;
                m.Btn("Desfazer", 10, y, 112, this, delegate { Undo(); });

                canvas.Location = new Point(132, 10); canvas.Size = new Size(cw, ch); canvas.BackColor = Color.Black;
                canvas.Paint += PaintCanvas; canvas.MouseDown += Down; canvas.MouseMove += Move2; canvas.MouseUp += Up;
                canvas.MouseCaptureChanged += delegate { if (!canvas.Capture) dragMode = -1; }; // ex.: Alt+Tab no meio do arrasto
                Controls.Add(canvas);
                var hint = m.L("Arraste para mover, puxe os cantos para redimensionar. Setas ajustam (Shift = 10 px). Delete exclui, Ctrl+Z desfaz, Ctrl+D duplica.", 132, ch + 16, this);
                hint.ForeColor = Color.Gray;
                m.Btn("Restaurar padrão", 132, ch + 40, 120, this, delegate
                {
                    int from = LayoutStore.BaseTheme(theme);
                    string what = from >= 0 ? "o tema " + ThemeNames[from] : "o padrao";
                    if (MessageBox.Show(this, "Trocar o layout atual por " + what + "?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    PushUndo(); items = from >= 0 ? m.ThemeToWidgets(from, land) : LayoutStore.Default(land); sel = null; Changed(); ShowProps();
                });
                m.Btn("Concluir", 132 + cw - 90, ch + 40, 90, this, delegate { Close(); });
                BuildProps(132 + cw + 12);
                int nx = 132 + cw + 12, ny = ClientSize.Height - 88;
                m.L("Nome do tema", nx, ny, this);
                var tName = m.Add(new TextBox(), nx, ny + 22, 212, this); tName.Text = LayoutStore.Name(theme); tName.MaxLength = 40;
                tName.TextChanged += delegate
                {
                    if (tName.Text.Trim() == "") return;
                    LayoutStore.Rename(theme, tName.Text); renamed = true; m.RefreshThemeLists();
                };
                var bDel = m.Btn("Excluir tema", nx, ny + 50, 104, this, delegate
                {
                    if (MessageBox.Show(this, "Excluir o tema \"" + LayoutStore.Name(theme) + "\"? Isso nao pode ser desfeito.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    DeleteRequested = true; Close();
                });
                bDel.Enabled = theme > LayoutStore.First;
                ShowProps();
                var t2 = new Timer(); t2.Interval = 1000; t2.Start();
                t2.Tick += delegate { Render(); LayoutStore.Save(); };
                FormClosed += delegate { t2.Stop(); t2.Dispose(); buffer.Dispose(); };
                Render();
            }

            void BuildProps(int x)
            {
                props = new Panel(); props.Location = new Point(x, 10); props.Size = new Size(218, ClientSize.Height - 104); Controls.Add(props);
                lblType = (Label)m.L("", 0, 0, props); lblType.Font = new Font(Font, FontStyle.Bold);
                nX = Num("X", 0, 28, 0); nY = Num("Y", 108, 28, 0); nW = Num("Larg.", 0, 58, 16); nH = Num("Alt.", 108, 58, 16);
                nX.ValueChanged += delegate { Apply(w => w.X = (int)nX.Value); }; nY.ValueChanged += delegate { Apply(w => w.Y = (int)nY.Value); };
                nW.ValueChanged += delegate { Apply(w => w.W = (int)nW.Value); }; nH.ValueChanged += delegate { Apply(w => w.H = (int)nH.Value); };
                lblSource = (Label)m.L("Dado", 0, 92, props);
                cbSource = m.Add(new ComboBox(), 48, 90, 164, props); cbSource.DropDownStyle = ComboBoxStyle.DropDownList;
                cbSource.SelectedIndexChanged += delegate { var s = cbSource.SelectedItem as SourceItem; if (s != null) Apply(w => w.Source = s.Id); };
                txt = m.Add(new TextBox(), 0, 90, 212, props); txt.Multiline = true; txt.Height = 58;
                txt.TextChanged += delegate { Apply(w => w.Text = txt.Text); };
                bImage = m.Btn("Escolher imagem...", 0, 90, 130, props, delegate
                {
                    if (sel == null) return;
                    var p = m.Open("Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
                    if (p != null) Apply(w => w.Path = p);
                });
                cFill = m.Chk("Preencher", true, 138, 94, props, delegate { Apply(w => w.Fill = cFill.Checked); });
                lblFont = (Label)m.L("Fonte", 0, 158, props);
                nFont = m.Add(new NumericUpDown(), 48, 156, 56, props); nFont.Maximum = 200;
                nFont.ValueChanged += delegate { Apply(w => w.Font = (int)nFont.Value); };
                m.L("0 = automática", 110, 158, props).ForeColor = Color.Gray;
                lblAlign = (Label)m.L("Alinhar", 0, 188, props);
                cbAlign = m.Add(new ComboBox(), 48, 186, 110, props); cbAlign.DropDownStyle = ComboBoxStyle.DropDownList;
                cbAlign.Items.AddRange(new object[] { "Esquerda", "Centro", "Direita" });
                cbAlign.SelectedIndexChanged += delegate { int i = cbAlign.SelectedIndex; if (i >= 0) Apply(w => w.Align = i); };
                bColor = m.Btn("Cor do texto", 0, 218, 104, props, delegate
                {
                    if (sel == null) return;
                    using (var d = new ColorDialog())
                    {
                        d.FullOpen = true; d.Color = Color.FromArgb(255, m.WidgetColor(sel.Color));
                        if (d.ShowDialog(this) == DialogResult.OK) { string c = ColorTranslator.ToHtml(Color.FromArgb(d.Color.R, d.Color.G, d.Color.B)); Apply(w => w.Color = c); ShowProps(); }
                    }
                });
                bTheme = m.Btn("Cor do tema", 108, 218, 104, props, delegate { Apply(w => w.Color = ""); ShowProps(); });
                cBg = m.Chk("Fundo", true, 0, 252, props, delegate { Apply(w => w.Bg = cBg.Checked); });
                cLabel = m.Chk("Mostrar rótulo", true, 70, 252, props, delegate { Apply(w => w.Label = cLabel.Checked); });
                cSec = m.Chk("Mostrar segundos", false, 0, 278, props, delegate { Apply(w => w.Seconds = cSec.Checked); });
                cBold = m.Chk("Negrito", false, 130, 278, props, delegate { Apply(w => w.Bold = cBold.Checked); });
                cThin = m.Chk("Fonte fina", false, 70, 252, props, delegate { Apply(w => w.Thin = cThin.Checked); });
                cBar = m.Chk("Mostrar barra", true, 0, 278, props, delegate { Apply(w => w.Bar = cBar.Checked); });
                cDetail = m.Chk("Mostrar detalhe", false, 104, 278, props, delegate { Apply(w => w.Detail = cDetail.Checked); });
                lblTokens = m.L("Valores ao vivo no texto:\n{cpu} {ram} {ramgb} {gpu}\n{cputemp} {gputemp}\n{down} {up} {ss} {title}\n{game} {place} {temp}\n{vpn} {fan} {gpufan}", 0, 278, props);
                lblTokens.ForeColor = Color.Gray;
            }

            NumericUpDown Num(string label, int x, int y, int min)
            {
                m.L(label, x, y + 2, props);
                var n = m.Add(new NumericUpDown(), x + 42, y, 58, props); n.Minimum = min; n.Maximum = 480;
                return n;
            }

            class SourceItem { public string Id, Name; public override string ToString() { return Name; } }

            IEnumerable<SourceItem> Sources(string type)
            {
                if (type == "specs")
                {
                    yield return new SourceItem { Id = "all", Name = "Resumo completo" };
                    foreach (var i in Specs.Items) yield return new SourceItem { Id = i[0], Name = i[2] };
                    yield return new SourceItem { Id = Specs.GpuRam[0], Name = Specs.GpuRam[2] };
                    yield break;
                }
                if (type == "graph")
                {
                    yield return new SourceItem { Id = "cpu", Name = "CPU" }; yield return new SourceItem { Id = "ram", Name = "Memória RAM" };
                    yield return new SourceItem { Id = "gpu", Name = "GPU" }; yield return new SourceItem { Id = "fps", Name = "FPS" };
                    yield break;
                }
                if (type == "battery")
                {
                    yield return new SourceItem { Id = "all", Name = "Todos os periféricos" };
                    foreach (var k in new[] { "mouse", "keyboard", "headset", "gamepad" })
                        yield return new SourceItem { Id = k, Name = "Só " + Batteries.KindName(k).ToLowerInvariant() };
                    yield break;
                }
                var pct = new HashSet<string> { "cpu", "ram", "gpu", "cputemp", "gputemp", "disk", "ssdtemp", "batmouse", "batkeyboard", "batheadset", "batgamepad" };
                for (int i = 0; i < CardIds.Length; i++)
                    if (type == "metric" || pct.Contains(CardIds[i])) yield return new SourceItem { Id = CardIds[i], Name = CardNames[i] };
                yield return new SourceItem { Id = "rampct", Name = "Memória RAM (%)" };
                if (type == "metric")
                {
                    yield return new SourceItem { Id = "ramgb", Name = "Memória RAM usada (GB)" };
                    yield return new SourceItem { Id = "fps", Name = "FPS" };
                }
            }

            void ShowProps()
            {
                loading = true;
                props.Visible = true;
                bool has = sel != null;
                foreach (Control c in props.Controls) c.Visible = has || c == lblType;
                if (!has) { lblType.Text = "Selecione um widget"; loading = false; return; }
                string t = sel.Type, name = t;
                foreach (var d in Types) if (d[0] == t) name = d[1];
                lblType.Text = name;
                nX.Value = Clamp(nX, sel.X); nY.Value = Clamp(nY, sel.Y); nW.Value = Clamp(nW, sel.W); nH.Value = Clamp(nH, sel.H);
                bool source = t == "metric" || t == "gauge" || t == "graph" || t == "specs" || t == "battery";
                lblSource.Visible = cbSource.Visible = source;
                if (source)
                {
                    cbSource.Items.Clear();
                    foreach (var s in Sources(t)) { cbSource.Items.Add(s); if (s.Id == sel.Source) cbSource.SelectedItem = s; }
                    if (cbSource.SelectedIndex < 0 && cbSource.Items.Count > 0) { cbSource.SelectedIndex = 0; sel.Source = ((SourceItem)cbSource.SelectedItem).Id; }
                }
                txt.Visible = t == "text"; if (t == "text") txt.Text = sel.Text;
                bImage.Visible = cFill.Visible = t == "image"; cFill.Checked = sel.Fill;
                bool hasFont = t == "clock" || t == "date" || t == "text" || t == "metric" || t == "gauge" || t == "weather" || t == "music" || t == "specs" || t == "battery" || t == "vpn";
                lblFont.Visible = nFont.Visible = hasFont; nFont.Value = Math.Min(200, sel.Font);
                foreach (Control c in props.Controls) if (c is Label && c.Text == "0 = automática") c.Visible = hasFont;
                bool align = t == "clock" || t == "date" || t == "text" || t == "metric" || t == "specs";
                lblAlign.Visible = cbAlign.Visible = align; cbAlign.SelectedIndex = Math.Max(0, Math.Min(2, sel.Align));
                bool color = t != "image" && t != "panel" && t != "forecast" && t != "quotes" && t != "player" && t != "agenda" && t != "fps";
                bColor.Visible = bTheme.Visible = color;
                bool picked = sel.Color.StartsWith("#");
                bColor.BackColor = picked ? m.WidgetColor(sel.Color) : SystemColors.Control;
                bColor.ForeColor = picked ? Contrast(m.WidgetColor(sel.Color)) : SystemColors.ControlText;
                cBg.Checked = sel.Bg; cLabel.Checked = sel.Label; cLabel.Visible = t == "metric" || t == "gauge" || t == "graph" || t == "specs" || t == "battery" || t == "vpn";
                cSec.Checked = sel.Seconds; cSec.Visible = t == "clock";
                cBold.Checked = sel.Bold; cBold.Visible = t == "clock";
                cThin.Checked = sel.Thin; cThin.Visible = t == "text";
                cBar.Checked = sel.Bar; cDetail.Checked = sel.Detail; cBar.Visible = cDetail.Visible = t == "metric";
                lblTokens.Visible = t == "text";
                loading = false;
            }

            static decimal Clamp(NumericUpDown n, int v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

            void Apply(Action<Widget> f)
            {
                if (loading || sel == null) return;
                PushUndo(); f(sel); Changed();
            }

            void PushUndo()
            {
                undo.Push(js.Serialize(items));
                if (undo.Count > 60) { var keep = new List<string>(undo); keep.RemoveAt(keep.Count - 1); keep.Reverse(); undo.Clear(); foreach (var s in keep) undo.Push(s); }
            }

            void Undo()
            {
                if (undo.Count == 0) return;
                items = js.Deserialize<List<Widget>>(undo.Pop()); sel = null; Changed(); ShowProps();
            }

            void Changed()
            {
                LayoutStore.Set(theme, land, new List<Widget>(items)); // o proximo quadro envia so o que mudou
                Render();
            }

            void Render()
            {
                if (buffer == null) return;
                using (var g = Graphics.FromImage(buffer))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    m.DrawBackground(g, sw, sh);
                    foreach (var w in items) m.DrawWidget(g, w);
                }
                canvas.Invalidate();
            }

            Rectangle Screen(Rectangle r) { return new Rectangle((int)(r.X * zoom), (int)(r.Y * zoom), (int)(r.Width * zoom), (int)(r.Height * zoom)); }

            Point[] Corners(Rectangle s) { return new[] { new Point(s.Left, s.Top), new Point(s.Right, s.Top), new Point(s.Left, s.Bottom), new Point(s.Right, s.Bottom) }; }

            void PaintCanvas(object o, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(buffer, new Rectangle(0, 0, canvas.Width, canvas.Height));
                g.PixelOffsetMode = PixelOffsetMode.Default;
                using (var faint = new Pen(Color.FromArgb(70, 255, 255, 255))) { faint.DashStyle = DashStyle.Dot; foreach (var w in items) g.DrawRectangle(faint, Screen(w.Rect)); }
                if (sel == null) return;
                var s = Screen(sel.Rect);
                using (var p = new Pen(Color.FromArgb(0, 200, 255), 2)) g.DrawRectangle(p, s);
                foreach (var c in Corners(s)) { g.FillRectangle(Brushes.White, c.X - 5, c.Y - 5, 10, 10); g.DrawRectangle(Pens.DodgerBlue, c.X - 5, c.Y - 5, 10, 10); }
            }

            int HitHandle(Point p)
            {
                if (sel == null) return -1;
                var cs = Corners(Screen(sel.Rect));
                for (int i = 0; i < 4; i++) if (Math.Abs(p.X - cs[i].X) <= 7 && Math.Abs(p.Y - cs[i].Y) <= 7) return i;
                return -1;
            }

            void Down(object o, MouseEventArgs e)
            {
                canvas.Focus();
                if (e.Button != MouseButtons.Left) return;
                int h = HitHandle(e.Location);
                if (h >= 0) dragMode = h + 1;
                else
                {
                    Widget hit = null; var pt = new Point((int)(e.X / zoom), (int)(e.Y / zoom));
                    for (int i = items.Count - 1; i >= 0; i--) if (items[i].Rect.Contains(pt)) { hit = items[i]; break; }
                    sel = hit; dragMode = hit != null ? 0 : -1; ShowProps(); canvas.Invalidate();
                }
                if (sel != null) { dragStart = e.Location; dragOrig = sel.Rect; dragPushed = false; }
            }

            static int Snap(int v) { return (int)Math.Round(v / 2.0) * 2; }

            void Move2(object o, MouseEventArgs e)
            {
                int h = HitHandle(e.Location);
                canvas.Cursor = h == 0 || h == 3 ? Cursors.SizeNWSE : h == 1 || h == 2 ? Cursors.SizeNESW : Cursors.Default;
                if (dragMode < 0 || sel == null) return;
                int dx = (int)((e.X - dragStart.X) / zoom), dy = (int)((e.Y - dragStart.Y) / zoom);
                if (dx == 0 && dy == 0) return;
                if (!dragPushed) { PushUndo(); dragPushed = true; }
                var r = dragOrig;
                if (dragMode == 0) { r.X = Snap(r.X + dx); r.Y = Snap(r.Y + dy); }
                else
                {
                    int l = r.Left, t = r.Top, ri = r.Right, b = r.Bottom;
                    if (dragMode == 1 || dragMode == 3) l = Snap(Math.Min(l + dx, ri - 16)); else ri = Snap(Math.Max(ri + dx, l + 16));
                    if (dragMode == 1 || dragMode == 2) t = Snap(Math.Min(t + dy, b - 16)); else b = Snap(Math.Max(b + dy, t + 16));
                    r = Rectangle.FromLTRB(l, t, ri, b);
                }
                SetRect(r);
            }

            void SetRect(Rectangle r)
            {
                r.Width = Math.Max(16, Math.Min(sw, r.Width)); r.Height = Math.Max(16, Math.Min(sh, r.Height));
                r.X = Math.Max(0, Math.Min(sw - r.Width, r.X)); r.Y = Math.Max(0, Math.Min(sh - r.Height, r.Y));
                sel.X = r.X; sel.Y = r.Y; sel.W = r.Width; sel.H = r.Height;
                loading = true;
                nX.Value = Clamp(nX, r.X); nY.Value = Clamp(nY, r.Y); nW.Value = Clamp(nW, r.Width); nH.Value = Clamp(nH, r.Height);
                loading = false;
                Changed();
            }

            void Up(object o, MouseEventArgs e) { dragMode = -1; }

            void AddWidget(string type)
            {
                PushUndo();
                var w = new Widget { Type = type, X = 16, Y = 16 };
                switch (type)
                {
                    case "clock": w.W = 180; w.H = 60; w.Bg = false; break;
                    case "date": w.W = 200; w.H = 26; w.Bg = false; break;
                    case "text": w.W = 150; w.H = 40; w.Bg = false; break;
                    case "metric": w.W = 110; w.H = 72; break;
                    case "gauge": w.W = 96; w.H = 96; break;
                    case "graph": w.W = 210; w.H = 90; break;
                    case "weather": w.W = 150; w.H = 72; break;
                    case "music": w.W = 230; w.H = 72; break;
                    case "specs": w.W = Math.Min(sw - 32, 260); w.H = 112; w.Source = "all"; break;
                    case "battery": w.W = Math.Min(sw - 32, 260); w.H = 120; w.Source = "all"; w.Label = true; break;
                    case "vpn": w.W = Math.Min(sw - 32, 260); w.H = 70; w.Label = false; break;
                    case "forecast": w.W = 224; w.H = 210; break;
                    case "quotes": w.W = 224; w.H = 190; break;
                    case "player": w.W = 320; w.H = 200; w.Bg = false; break;
                    case "agenda": w.W = 300; w.H = 190; w.Bg = false; break;
                    case "fps": w.W = 190; w.H = 132; break;
                    case "image": w.W = 110; w.H = 110; w.Bg = false; break;
                    case "panel": w.W = 170; w.H = 110; break;
                }
                items = new List<Widget>(items); items.Add(w); sel = w; Changed(); ShowProps(); canvas.Focus();
            }

            void Duplicate()
            {
                if (sel == null) return;
                PushUndo();
                var c = js.Deserialize<Widget>(js.Serialize(sel));
                c.X = Math.Min(sw - c.W, c.X + 10); c.Y = Math.Min(sh - c.H, c.Y + 10);
                items = new List<Widget>(items); items.Add(c); sel = c; Changed(); ShowProps();
            }

            void Delete()
            {
                if (sel == null) return;
                PushUndo();
                items = new List<Widget>(items); items.Remove(sel); sel = null; Changed(); ShowProps();
            }

            void Reorder(bool front)
            {
                if (sel == null) return;
                PushUndo();
                items = new List<Widget>(items); items.Remove(sel);
                if (front) items.Add(sel); else items.Insert(0, sel);
                Changed();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys k)
            {
                if (ActiveControl == canvas)
                {
                    Keys code = k & Keys.KeyCode; int step = (k & Keys.Shift) != 0 ? 10 : 1;
                    if (code == Keys.Delete) { Delete(); return true; }
                    if (k == (Keys.Control | Keys.Z)) { Undo(); return true; }
                    if (k == (Keys.Control | Keys.D)) { Duplicate(); return true; }
                    int dx = code == Keys.Left ? -step : code == Keys.Right ? step : 0, dy = code == Keys.Up ? -step : code == Keys.Down ? step : 0;
                    if (sel != null && (dx != 0 || dy != 0))
                    {
                        PushUndo();
                        var r = sel.Rect; r.Offset(dx, dy); SetRect(r);
                        return true;
                    }
                }
                return base.ProcessCmdKey(ref msg, k);
            }
        }
    }
}
