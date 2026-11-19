using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace TurzxScreen
{
    // Fonte de tudo que e desenhado na tela. O codigo pede "Segoe UI", "Segoe UI Semibold" ou
    // "Segoe UI Light"; com outra familia escolhida, o peso vira negrito ou fino na familia nova.
    static class SF
    {
        public const string Default = "Segoe UI";
        static volatile string family = "";   // "" = Segoe UI (padrao)
        static volatile bool hasLight, hasSemi;
        public static volatile int Version;    // muda a cada troca: quem guarda medidas refaz

        public static string Family
        {
            get { return family == "" ? Default : family; }
            set
            {
                string v = (value ?? "").Trim();
                if (v.Equals(Default, StringComparison.OrdinalIgnoreCase) || !Installed(v)) v = "";
                if (v == family) return;
                hasLight = v != "" && Installed(v + " Light");
                hasSemi = v != "" && Installed(v + " Semibold");
                family = v; Version++;
            }
        }

        public static bool IsDefault { get { return family == ""; } }

        static HashSet<string> names;
        public static bool Installed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (names == null)
            {
                var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { using (var c = new InstalledFontCollection()) foreach (var f in c.Families) h.Add(f.Name); } catch { }
                names = h;
            }
            return names.Contains(name);
        }

        public static List<string> List()
        {
            var r = new List<string>();
            try { using (var c = new InstalledFontCollection()) foreach (var f in c.Families) if (f.IsStyleAvailable(FontStyle.Regular)) r.Add(f.Name); } catch { }
            return r;
        }

        public static Font New(string name, float px)
        {
            string fam = family;
            if (fam == "") return new Font(name, px, GraphicsUnit.Pixel);
            bool semi = name.EndsWith("Semibold", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Bold", StringComparison.OrdinalIgnoreCase);
            bool light = name.EndsWith("Light", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (light && hasLight) return new Font(fam + " Light", px, GraphicsUnit.Pixel);
                if (semi && hasSemi) return new Font(fam + " Semibold", px, GraphicsUnit.Pixel);
                return new Font(fam, px, semi ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch { return new Font(name, px, GraphicsUnit.Pixel); }
        }
    }
}
