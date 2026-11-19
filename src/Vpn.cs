using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace TurzxScreen
{
    class VpnConn
    {
        public string Key = "", App = "", Profile = "";
        public DateTime Since = DateTime.Now;   // conectada desde (ou desde que o app abriu)
        public double Down = -1, Up = -1;       // bytes/s pela placa da VPN
        public long RxBytes, TxBytes; public DateTime At;
        public string Sig { get { return Key + "|" + App + "|" + Profile; } }
    }

    // VPN conectada: cada cliente cria a propria placa de rede virtual, e ela so recebe
    // endereco de verdade (fora de 169.254) quando o tunel esta no ar. So le, nada e alterado.
    static class Vpn
    {
        public static volatile List<VpnConn> List = new List<VpnConn>();
        public static volatile bool Enabled = true;
        public static event Action<VpnConn, bool> Changed; // true = conectou
        static Thread worker;
        static bool first = true;
        static string ovpnPids = "";
        static List<string[]> ovpnCache = new List<string[]>(); // [app, perfil]

        public static void Start()
        {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "vpn", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        static void Loop()
        {
            Thread.Sleep(2000);
            while (true)
            {
                if (Enabled)
                    try { Poll(); } catch (Exception ex) { Log.Error("vpn", ex); }
                Thread.Sleep(4000);
            }
        }

        static bool HasRealAddress(NetworkInterface n)
        {
            foreach (var a in n.GetIPProperties().UnicastAddresses)
            {
                var ip = a.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var b = ip.GetAddressBytes();
                    if (!(b[0] == 169 && b[1] == 254) && b[0] != 127) return true;
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal && !ip.Equals(System.Net.IPAddress.IPv6Loopback))
                    return true;
            }
            return false;
        }

        static void Poll()
        {
            var res = new List<VpnConn>();
            List<string[]> ovpn = null;
            int ovpnNext = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string desc = (n.Description ?? "").ToLowerInvariant(), both = ((n.Name ?? "") + " " + desc).ToLowerInvariant();
                string app = null, profile = "";
                if (desc.Contains("fortinet")) app = "FortiClient VPN";
                else if (desc.Contains("check point")) app = "Check Point VPN";
                else if (desc.Contains("sonicwall")) app = "SonicWall Global VPN";
                else if (desc.Contains("draytek")) app = "DrayTek Smart VPN";
                else if (desc.Contains("pangp") || desc.Contains("globalprotect")) app = "GlobalProtect";
                else if (desc.Contains("watchguard")) app = "WatchGuard SSL VPN";
                else if (desc.Contains("tap-windows") || desc.Contains("wintun") || desc.Contains("data channel offload") || desc.Contains("openvpn"))
                {
                    if (both.Contains("openvpn connect")) app = "OpenVPN Connect";
                    else
                    {
                        if (!HasRealAddress(n)) continue;
                        if (ovpn == null) ovpn = OpenVpnProcesses();
                        if (ovpnNext < ovpn.Count) { app = ovpn[ovpnNext][0]; profile = ovpn[ovpnNext][1]; ovpnNext++; }
                        else app = Process.GetProcessesByName("wgsslvpnc").Length > 0 ? "WatchGuard SSL VPN" : "OpenVPN";
                    }
                }
                else if (n.NetworkInterfaceType == NetworkInterfaceType.Ppp) { app = "VPN do Windows"; profile = n.Name; }
                else if (Regex.IsMatch(desc, @"\bvpn\b")) app = n.Description;
                if (app == null || !HasRealAddress(n)) continue;
                var c = new VpnConn { Key = n.Id, App = app, Profile = profile ?? "", At = DateTime.Now };
                try { var st = n.GetIPStatistics(); c.RxBytes = st.BytesReceived; c.TxBytes = st.BytesSent; } catch { }
                res.Add(c);
            }
            res.Sort((a, b) => string.Compare(a.App + a.Profile, b.App + b.Profile, StringComparison.Ordinal));

            var old = List;
            foreach (var c in res)
                foreach (var o in old)
                    if (o.Sig == c.Sig)
                    {
                        c.Since = o.Since;
                        double s = (c.At - o.At).TotalSeconds;
                        if (s > 0.5 && c.RxBytes >= o.RxBytes && c.TxBytes >= o.TxBytes) { c.Down = (c.RxBytes - o.RxBytes) / s; c.Up = (c.TxBytes - o.TxBytes) / s; }
                        break;
                    }
            List = res;
            if (first) { first = false; return; }
            var h = Changed;
            if (h == null) return;
            var was = new HashSet<string>(); foreach (var c in old) was.Add(c.Sig);
            var now = new HashSet<string>(); foreach (var c in res) now.Add(c.Sig);
            foreach (var c in res) if (!was.Contains(c.Sig)) try { h(c, true); } catch (Exception ex) { Log.Error("vpn aviso", ex); }
            foreach (var c in old) if (!now.Contains(c.Sig)) try { h(c, false); } catch (Exception ex) { Log.Error("vpn aviso", ex); }
        }

        // openvpn.exe em execucao: de quem e (OpenVPN GUI, WatchGuard) e qual perfil (--config)
        static List<string[]> OpenVpnProcesses()
        {
            var procs = Process.GetProcessesByName("openvpn");
            var ids = new List<int>(); foreach (var p in procs) { ids.Add(p.Id); p.Dispose(); }
            ids.Sort();
            string key = string.Join(",", ids.ConvertAll(i => i.ToString()).ToArray());
            if (key == ovpnPids) return ovpnCache;
            var r = new List<string[]>();
            if (ids.Count > 0)
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name='openvpn.exe'"))
                        foreach (ManagementObject mo in s.Get())
                        {
                            string path = Convert.ToString(mo["ExecutablePath"]) ?? "", cmd = Convert.ToString(mo["CommandLine"]) ?? "";
                            string app = path.IndexOf("WatchGuard", StringComparison.OrdinalIgnoreCase) >= 0 ? "WatchGuard SSL VPN"
                                : cmd.Contains("OpenVPN GUI") || path.IndexOf(@"\OpenVPN\bin", StringComparison.OrdinalIgnoreCase) >= 0 ? "OpenVPN GUI" : "OpenVPN";
                            var m = Regex.Match(cmd, "--config\\s+(?:\"([^\"]+)\"|(\\S+))");
                            string prof = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "";
                            prof = System.IO.Path.GetFileNameWithoutExtension(prof);
                            r.Add(new[] { app, prof });
                        }
                }
                catch (Exception ex) { Log.Error("vpn openvpn", ex); }
            // o OpenVPN GUI primeiro: e o que usa as placas TAP/Wintun comuns
            r.Sort((a, b) => (a[0] == "OpenVPN GUI" ? 0 : 1).CompareTo(b[0] == "OpenVPN GUI" ? 0 : 1));
            ovpnPids = key; ovpnCache = r;
            return r;
        }

        public static string Describe(VpnConn c, bool hideProfile)
        {
            return Place(c, hideProfile) + (Program(c, hideProfile) != "" ? " · " + Program(c, hideProfile) : "");
        }

        // o que importa e onde se esta conectado (perfil/conexao); o programa vem depois
        public static string Place(VpnConn c, bool hideProfile) { return !hideProfile && c.Profile != "" ? c.Profile : c.App; }
        public static string Program(VpnConn c, bool hideProfile) { return !hideProfile && c.Profile != "" ? c.App : ""; }

        public static string Duration(VpnConn c)
        {
            var d = DateTime.Now - c.Since;
            if (d.TotalMinutes < 1) return "agora";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min";
            if (d.TotalDays < 1) return (int)d.TotalHours + " h " + d.Minutes.ToString("00");
            return (int)d.TotalDays + " d " + d.Hours + " h";
        }
    }
}
