// Log de erros, sensores do LibreHardwareMonitor e deteccao de microfone/camera em uso.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TurzxScreen
{
    // ------------------------------------------------------------------ Log
    // turzx.log ao lado do .exe; nao repete a mesma mensagem e nunca guarda conteudo de notificacoes.
    static class Log
    {
        const long MaxBytes = 256 * 1024;
        static readonly object sync = new object();
        static readonly Dictionary<string, string> last = new Dictionary<string, string>();
        static string FilePath { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "turzx.log"); } }

        public static void Write(string area, string msg)
        {
            try
            {
                lock (sync)
                {
                    string prev;
                    if (last.TryGetValue(area, out prev) && prev == msg) return;
                    last[area] = msg;
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes) File.Copy(FilePath, FilePath + ".1", true);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + area + "] " + msg + Environment.NewLine;
                    if (fi.Exists && fi.Length > MaxBytes) File.WriteAllText(FilePath, line);
                    else File.AppendAllText(FilePath, line);
                }
            }
            catch { }
        }

        public static void Error(string area, Exception ex)
        {
            var e = ex.InnerException ?? ex;
            Write(area, e.GetType().Name + ": " + e.Message);
        }
    }

    // ------------------------------------------------------------------ LibreHardwareMonitor
    // Preenchido a partir do data.json; -1 = sensor indisponivel.
    static class Lhm
    {
        public static volatile bool Available;
        public static double GpuLoad = -1, GpuTemp = -1, GpuClock = -1, GpuPower = -1, GpuFan = -1;
        public static double CpuClockGhz = -1, CpuPower = -1, CaseFan = -1, SsdTemp = -1;

        class Drive { public double Used = -1, Temp = -1; }
        static double gLoad, gTemp, gClock, gPower, gFan, cClock, cPower, fan;
        static Dictionary<string, Drive> drives;

        public static void Begin()
        {
            gLoad = gTemp = gClock = gPower = gFan = cClock = cPower = fan = -1;
            drives = new Dictionary<string, Drive>();
        }

        public static void Feed(string id, string type, string name, double v)
        {
            if (id.StartsWith("/intelcpu/") || id.StartsWith("/amdcpu/"))
            {
                if (type == "Clock" && name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 && v > cClock) cClock = v;
                else if (type == "Power" && name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0) cPower = v;
            }
            else if (id.StartsWith("/gpu-") && !id.Contains("integrated"))
            {
                if (type == "Load" && name == "GPU Core") gLoad = v;
                else if (type == "Temperature" && name == "GPU Core") gTemp = v;
                else if (type == "Clock" && name == "GPU Core") gClock = v;
                else if (type == "Power" && Regex.IsMatch(name, "Package|Board|GPU Power", RegexOptions.IgnoreCase)) gPower = v;
                else if (type == "Fan" && v > gFan) gFan = v;
            }
            else if (id.StartsWith("/lpc/"))
            {
                if (type == "Fan" && v > fan) fan = v;
            }
            else
            {
                var m = Regex.Match(id, @"^(/(?:nvme|ssd|hdd|ata|storage)[^/]*/[^/]+)/(temperature|load)/");
                if (!m.Success) return;
                Drive d;
                if (!drives.TryGetValue(m.Groups[1].Value, out d)) drives[m.Groups[1].Value] = d = new Drive();
                if (type == "Load" && name == "Used Space") d.Used = v;
                else if (type == "Temperature" && d.Temp < 0 && !Regex.IsMatch(name, "Warning|Critical", RegexOptions.IgnoreCase)) d.Temp = v;
            }
        }

        public static void Commit(double systemDiskPct)
        {
            // o SSD do sistema e o que tem o mesmo percentual de uso do disco C:
            double best = 3, ssd = -1;
            foreach (var d in drives.Values)
            {
                if (d.Used < 0 || d.Temp < 0) continue;
                double diff = Math.Abs(d.Used - systemDiskPct);
                if (diff <= best) { best = diff; ssd = d.Temp; }
            }
            GpuLoad = gLoad; GpuTemp = gTemp; GpuClock = gClock; GpuPower = gPower; GpuFan = gFan;
            CpuClockGhz = cClock >= 0 ? cClock / 1000 : -1; CpuPower = cPower; CaseFan = fan; SsdTemp = ssd;
            Available = true;
        }

        public static void Clear()
        {
            Available = false;
            GpuLoad = GpuTemp = GpuClock = GpuPower = GpuFan = CpuClockGhz = CpuPower = CaseFan = SsdTemp = -1;
        }
    }

    // ------------------------------------------------------------------ Microfone / camera
    // O Windows registra o uso em CapabilityAccessManager, mas deixa entradas antigas com
    // LastUsedTimeStop = 0 (app fechou de forma abrupta). Por isso cada candidato e validado.
    static class OnAir
    {
        public static volatile bool WatchMic = true, WatchCam = true;
        public static volatile string Ignore = "";
        public static volatile bool Mic, Cam;
        public static volatile string MicApp = "", CamApp = "";
        public static DateTime TestUntil = DateTime.MinValue;
        static Thread th;
        static DateTime boot = DateTime.MinValue;
        const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public static bool Testing { get { return DateTime.Now < TestUntil; } }
        public static bool ShowMic { get { return Testing || (WatchMic && Mic); } }
        public static bool ShowCam { get { return !Testing && WatchCam && Cam; } }
        public static bool Active { get { return ShowMic || ShowCam; } }

        public static void Start()
        {
            if (th != null) return;
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        static void Loop()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                    foreach (ManagementObject mo in s.Get())
                        boot = ManagementDateTimeConverter.ToDateTime(Convert.ToString(mo["LastBootUpTime"]));
            }
            catch (Exception ex) { Log.Error("onair", ex); }
            while (true)
            {
                try
                {
                    string app = Find("microphone"); Mic = app != null; MicApp = app ?? "";
                    app = Find("webcam"); Cam = app != null; CamApp = app ?? "";
                }
                catch (Exception ex) { Log.Error("onair", ex); }
                Thread.Sleep(1500);
            }
        }

        static string Find(string capability)
        {
            using (var root = Registry.CurrentUser.OpenSubKey(Root + capability))
            {
                if (root == null) return null;
                foreach (var name in root.GetSubKeyNames())
                {
                    if (name == "NonPackaged") continue;
                    DateTime since;
                    using (var k = root.OpenSubKey(name))
                        if (!InUse(k, out since)) continue;
                    string shown = Friendly(name.Split('_')[0]);
                    if (!Ignored(shown, name)) return shown;
                }
                using (var np = root.OpenSubKey("NonPackaged"))
                {
                    if (np == null) return null;
                    foreach (var name in np.GetSubKeyNames())
                    {
                        DateTime since;
                        using (var k = np.OpenSubKey(name))
                            if (!InUse(k, out since)) continue;
                        string path = name.Replace('#', '\\');
                        if (!ProcessAlive(path, since)) continue;
                        string shown = Friendly(Path.GetFileNameWithoutExtension(path));
                        if (!Ignored(shown, name)) return shown;
                    }
                }
            }
            return null;
        }

        static bool InUse(RegistryKey k, out DateTime since)
        {
            since = DateTime.MinValue;
            if (k == null) return false;
            object s = k.GetValue("LastUsedTimeStart"), e = k.GetValue("LastUsedTimeStop");
            if (!(s is long) || !(e is long) || (long)e != 0 || (long)s <= 0) return false;
            since = DateTime.FromFileTime((long)s);
            return since >= boot; // entrada de antes do boot nao pode estar ativa
        }

        static bool ProcessAlive(string path, DateTime since)
        {
            bool alive = false;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                try
                {
                    if (!alive && p.StartTime <= since.AddSeconds(5))
                    {
                        string real = null;
                        try { real = p.MainModule.FileName; } catch { }
                        alive = real == null || string.Equals(real, path, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return alive;
        }

        static bool Ignored(string shown, string raw)
        {
            foreach (var part in (Ignore ?? "").Split(','))
            {
                string p = part.Trim();
                if (p == "") continue;
                if (shown.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static string Friendly(string n)
        {
            string a = n.ToLowerInvariant();
            if (a.Contains("teams")) return "Teams";
            if (a.Contains("discord")) return "Discord";
            if (a.Contains("zoom")) return "Zoom";
            if (a.Contains("chrome")) return "Chrome";
            if (a.Contains("msedge")) return "Edge";
            if (a.Contains("firefox")) return "Firefox";
            if (a.StartsWith("obs")) return "OBS";
            if (a.Contains("whatsapp")) return "WhatsApp";
            if (a.Contains("soundrecorder")) return "Gravador de Som";
            if (a.Contains("windowscamera")) return "Camera";
            int dot = n.LastIndexOf('.');
            return dot >= 0 && dot < n.Length - 1 ? n.Substring(dot + 1) : n;
        }
    }
}
