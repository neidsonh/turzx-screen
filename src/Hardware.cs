// Especificacoes da maquina e temperatura da CPU.
// Temperatura da CPU: o Windows nao expoe sem driver/admin. Lemos do LibreHardwareMonitor
// (ou OpenHardwareMonitor) via WMI quando ele estiver aberto.
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace TurzxScreen
{
    static class Specs
    {
        public static string Cpu = "", CpuCores = "", Gpu = "", GpuMem = "", Ram = "", RamType = "", Board = "", Os = "", Disk = "";
        public static bool Loaded;

        public static void LoadAsync() { ThreadPool.QueueUserWorkItem(delegate { Load(); }); }

        static void Load()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    Cpu = ShortCpu(Convert.ToString(k.GetValue("ProcessorNameString")));
            }
            catch { }
            try
            {
                foreach (ManagementObject mo in new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").Get())
                { CpuCores = mo["NumberOfCores"] + "C/" + mo["NumberOfLogicalProcessors"] + "T"; break; }
            }
            catch { }
            try
            {
                // placa de video: a de maior memoria que nao seja adaptador basico/virtual
                ulong best = 0;
                foreach (ManagementObject mo in new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController").Get())
                {
                    string n = Convert.ToString(mo["Name"]);
                    if (Regex.IsMatch(n, "Basic|Virtual|Remote|Parsec|Meta", RegexOptions.IgnoreCase)) continue;
                    ulong ram = 0; try { ram = Convert.ToUInt64(mo["AdapterRAM"]); } catch { }
                    if (Gpu == "" || ram > best) { best = ram; Gpu = ShortGpu(n); }
                }
            }
            catch { }
            try
            {
                ulong total = 0; int type = 0; uint speed = 0;
                foreach (ManagementObject mo in new ManagementObjectSearcher("SELECT Capacity, SMBIOSMemoryType, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory").Get())
                {
                    total += Convert.ToUInt64(mo["Capacity"]);
                    try { type = Convert.ToInt32(mo["SMBIOSMemoryType"]); } catch { }
                    try { speed = Convert.ToUInt32(mo["ConfiguredClockSpeed"] ?? mo["Speed"]); } catch { }
                }
                Ram = Math.Round(total / 1073741824.0) + " GB";
                string t = type == 34 ? "DDR5" : type == 26 ? "DDR4" : type == 24 ? "DDR3" : type == 35 ? "LPDDR5" : type == 30 ? "LPDDR4" : "";
                RamType = (t + (speed > 0 ? " " + speed + " MHz" : "")).Trim();
            }
            catch { }
            try
            {
                foreach (ManagementObject mo in new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard").Get())
                {
                    string m = Convert.ToString(mo["Manufacturer"]);
                    m = Regex.Replace(m, @"\s*(Technology|Co\.|Ltd\.?|Inc\.?|Corporation|Computer|International|,)\s*", " ", RegexOptions.IgnoreCase).Trim();
                    Board = (m.Split(' ')[0] + " " + mo["Product"]).Trim(); break;
                }
            }
            catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = Convert.ToString(k.GetValue("ProductName"));
                    int build; int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build);
                    if (build >= 22000) name = name.Replace("Windows 10", "Windows 11"); // o registro ainda diz "10" no 11
                    Os = name.Replace("Microsoft ", "");
                }
            }
            catch { }
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                double gb = d.TotalSize / 1e9;
                Disk = gb >= 1000 ? (gb / 1000).ToString("0.#") + " TB" : gb.ToString("0") + " GB";
            }
            catch { }
            Loaded = true;
        }

        public static string ShortCpu(string s)
        {
            s = Regex.Replace(s ?? "", @"\(R\)|\(TM\)|\bCPU\b|@.*$|\b\d+(th|st|nd|rd) Gen\b|\b\d+-Core\b|\bProcessor\b|\bwith Radeon.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string ShortGpu(string s)
        {
            s = Regex.Replace(s ?? "", @"^NVIDIA\s+|^AMD\s+|\(R\)|\(TM\)|\bGraphics\b$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        // id, rotulo curto, nome completo (widget "Maquina" do editor)
        public static readonly string[][] Items = {
            new[] { "cpu", "CPU", "Processador" }, new[] { "gpu", "GPU", "Placa de vídeo" }, new[] { "ram", "RAM", "Memória RAM" },
            new[] { "board", "Placa", "Placa-mãe" }, new[] { "os", "Sistema", "Sistema" }, new[] { "disk", "Disco", "Disco do sistema" } };

        public static readonly string[] GpuRam = { "gpuram", "Hardware", "Placa de vídeo e memória" };

        public static string[] Find(string id)
        {
            foreach (var i in Items) if (i[0] == id) return i;
            return id == GpuRam[0] ? GpuRam : null;
        }

        public static string Value(string id)
        {
            switch (id)
            {
                case "cpu": return (Cpu + " " + CpuCores).Trim();
                case "gpu": return (Gpu + " " + GpuMem).Trim();
                case "ram": return (Ram + " " + RamType).Trim();
                case "board": return Board;
                case "os": return Os;
                case "disk": return Disk;
                case "gpuram": return Line2();
            }
            return "";
        }

        // ex.: "Core i7-11700K · RTX 4060 · 32 GB"
        public static string Line1() { return Join(Cpu, CpuCores); }
        public static string Line2() { return Join(Gpu + (GpuMem != "" ? " " + GpuMem : ""), Ram + (RamType != "" ? " " + RamType.Split(' ')[0] : "")); }
        static string Join(params string[] p)
        {
            var l = new List<string>(); foreach (var s in p) if (!string.IsNullOrEmpty(s) && s.Trim() != "") l.Add(s.Trim());
            return string.Join("  ·  ", l.ToArray());
        }
    }

    static class CpuTemp
    {
        public static double Value = -1;
        public static string Source = "";
        static DateTime next = DateTime.MinValue;
        static int busy;

        public static void Poll()
        {
            if (DateTime.Now < next || Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { next = DateTime.Now.AddSeconds(Read() ? 2 : 15); }
                finally { busy = 0; }
            });
        }

        // LibreHardwareMonitor 0.9.x: servidor web local (Options > Remote Web Server), sem WMI
        static bool ReadWeb()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:8085/data.json");
                req.Timeout = 1500; req.ReadWriteTimeout = 1500; req.Proxy = null;
                string json;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8)) json = sr.ReadToEnd();
                var root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .Deserialize<Dictionary<string, object>>(json);
                double package = -1, maxCore = -1;
                Lhm.Begin();
                Walk(root, ref package, ref maxCore);
                Lhm.Commit(SysInfo.DiskPct);
                double t = package >= 0 ? package : maxCore;
                if (t > 0) { Value = t; Source = "LibreHardwareMonitor"; return true; }
            }
            catch { Lhm.Clear(); }
            return false;
        }

        static void Walk(Dictionary<string, object> n, ref double package, ref double maxCore)
        {
            object sid, typeObj;
            if (n.TryGetValue("SensorId", out sid) && n.TryGetValue("Type", out typeObj))
            {
                string id = Convert.ToString(sid).ToLowerInvariant(), type = Convert.ToString(typeObj);
                string name = Convert.ToString(n["Text"]);
                // "Value": "45,0 °C" (formato da cultura do LHM)
                var m = Regex.Match(Convert.ToString(n["Value"]), @"-?\d+(?:[.,]\d+)?");
                double v;
                if (m.Success && double.TryParse(m.Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    Lhm.Feed(id, type, name, v);
                    if (type == "Temperature" && id.Contains("cpu/"))
                    {
                        if (Regex.IsMatch(name, "Package|Tctl|Tdie", RegexOptions.IgnoreCase)) package = v;
                        else if (name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 && !name.Contains("Distance") && !name.Contains("Average") && v > maxCore) maxCore = v;
                    }
                }
            }
            object kids;
            if (n.TryGetValue("Children", out kids) && kids is System.Collections.ArrayList)
                foreach (var k in (System.Collections.ArrayList)kids)
                {
                    var d = k as Dictionary<string, object>;
                    if (d != null) Walk(d, ref package, ref maxCore);
                }
        }

        static bool Read()
        {
            if (ReadWeb()) return true;
            foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
            {
                try
                {
                    double package = -1, maxCore = -1;
                    var q = new ManagementObjectSearcher(ns, "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'");
                    foreach (ManagementObject mo in q.Get())
                    {
                        string id = Convert.ToString(mo["Identifier"]).ToLowerInvariant();
                        if (!id.Contains("cpu")) continue;
                        string name = Convert.ToString(mo["Name"]);
                        double v = Convert.ToDouble(mo["Value"]);
                        if (Regex.IsMatch(name, "Package|Tctl|Tdie", RegexOptions.IgnoreCase)) package = v;
                        else if (name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && v > maxCore) maxCore = v;
                    }
                    double t = package >= 0 ? package : maxCore;
                    if (t > 0) { Value = t; Source = ns.Contains("Libre") ? "LibreHardwareMonitor" : "OpenHardwareMonitor"; return true; }
                }
                catch { }
            }
            Value = -1; Source = "";
            return false;
        }
    }
}
