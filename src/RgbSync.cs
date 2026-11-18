// Cor do RGB do PC: le, sem alterar nada, o efeito escolhido no SignalRGB.
// O SignalRGB guarda os ajustes no registro do usuario (QSettings). Esse formato nao e
// uma interface oficial: se mudar, a leitura falha e as cores do painel ficam como estao.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace TurzxScreen
{
    class RgbInfo
    {
        public bool Found, HasColor;
        public string Effect = "";
        public Color Color;
    }

    static class RgbSync
    {
        public const string DefaultKey = @"Software\WhirlwindFX\SignalRgb";

        public static RgbInfo Read(string root)
        {
            var info = new RgbInfo();
            try
            {
                using (var sel = Registry.CurrentUser.OpenSubKey(root + @"\effects\selected"))
                {
                    if (sel == null) return info;
                    string id = sel.GetValue("id") as string;
                    info.Effect = (sel.GetValue("name") as string) ?? "";
                    if (string.IsNullOrEmpty(id)) return info;
                    info.Found = true;
                    if (info.Effect == "") info.Effect = id;
                    using (var fx = Registry.CurrentUser.OpenSubKey(root + @"\effects\" + id))
                        if (fx != null) Pick(fx, info);
                }
            }
            catch { info.HasColor = false; }
            return info;
        }

        // a cor principal do efeito; cores de fundo nao servem de destaque
        static void Pick(RegistryKey fx, RgbInfo info)
        {
            var names = new List<string>(fx.GetValueNames());
            names.Sort(StringComparer.OrdinalIgnoreCase);
            int best = int.MaxValue;
            foreach (string n in names)
            {
                string low = n.ToLowerInvariant();
                if (low.Contains("bg") || low.Contains("back")) continue;
                Color c;
                if (!Decode(fx.GetValue(n), out c)) continue;
                int rank = low == "color" ? 0 : low.StartsWith("color") ? 1 : low.Contains("color") ? 2 : 3;
                if (rank < best) { best = rank; info.Color = c; info.HasColor = true; }
            }
        }

        // @Variant( 00 00 00 43 | espec | alfa, 3 componentes e reserva em 16 bits | )
        public static bool Decode(object value, out Color color)
        {
            color = Color.Empty;
            string text = value as string;
            if (text != null)
            {
                if (text.Length != 7 || text[0] != '#') return false;
                int rgb;
                if (!int.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
                color = Color.FromArgb(255, Color.FromArgb(rgb));
                return true;
            }
            byte[] raw = value as byte[];
            if (raw == null) return false;
            string s = Encoding.Unicode.GetString(raw);
            const string prefix = "@Variant(";
            if (!s.StartsWith(prefix, StringComparison.Ordinal) || s.Length < prefix.Length + 15) return false;
            int p = prefix.Length;
            if (s[p] != 0 || s[p + 1] != 0 || s[p + 2] != 0 || s[p + 3] != 67) return false; // 67 = QColor
            int spec = s[p + 4];
            int a = Word(s, p + 7), b = Word(s, p + 9), c = Word(s, p + 11);
            double hue = a == 0xFFFF ? 0 : a / 100.0;
            if (spec == 1) color = Color.FromArgb(a / 257, b / 257, c / 257);
            else if (spec == 2) color = FromHsv(hue, b / 65535.0, c / 65535.0);
            else if (spec == 4) color = FromHsl(hue, b / 65535.0, c / 65535.0);
            else return false;
            return true;
        }

        static int Word(string s, int i) { return ((s[i] & 255) << 8) | (s[i + 1] & 255); }

        // destaque na cor do RGB, com fundo escuro e texto claro do mesmo tom
        public static void Harmonize(Color rgb, out Color bg, out Color accent, out Color fg)
        {
            double h = rgb.GetHue(), s = rgb.GetSaturation(), l = rgb.GetBrightness();
            accent = FromHsl(h, s, Math.Max(0.50, Math.Min(0.72, l)));
            bg = FromHsl(h, Math.Min(s, 0.35), 0.07);
            fg = FromHsl(h, Math.Min(s, 0.25), 0.94);
        }

        public static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            return FromChroma(h, c, l - c / 2);
        }

        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s;
            return FromChroma(h, c, v - c);
        }

        static Color FromChroma(double h, double c, double m)
        {
            double k = ((h % 360) + 360) % 360 / 60;
            double x = c * (1 - Math.Abs(k % 2 - 1)), r = 0, g = 0, b = 0;
            if (k < 1) { r = c; g = x; }
            else if (k < 2) { r = x; g = c; }
            else if (k < 3) { g = c; b = x; }
            else if (k < 4) { g = x; b = c; }
            else if (k < 5) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(Byte255(r + m), Byte255(g + m), Byte255(b + m));
        }

        static int Byte255(double v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v * 255))); }
    }
}
