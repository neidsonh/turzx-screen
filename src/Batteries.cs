using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TurzxScreen
{
    class BatteryDevice
    {
        public string Key = "", Name = "", Kind = "other", Via = "";
        public int Percent = -1;
        public bool Charging, Asleep;
        public DateTime Seen;
    }

    // Bateria dos perifericos sem fio. So le: nada e escrito nos aparelhos.
    //  - Bluetooth: o nivel que o proprio Windows guarda (o mesmo de Configuracoes > Bluetooth),
    //    so para aparelhos conectados agora.
    //  - DualSense / DualShock 4: relatorio de entrada HID (Linux hid-playstation.c).
    //  - Receptores Xenta 1D57 (Attack Shark, Delux, Inphic, ARDOR): o receptor anuncia a bateria
    //    sozinho a cada ~2 s, so escutamos.
    //  - Controles Xbox: XInputGetBatteryInformation.
    // Protocolos baseados em github.com/krisgfx9/Wireless-Device-Battery-Monitor (GPL-3.0).
    static class Batteries
    {
        public static volatile List<BatteryDevice> List = new List<BatteryDevice>();
        public static volatile bool Enabled = true;
        public static event Action<BatteryDevice> Low;
        static readonly Dictionary<string, BatteryDevice> keep = new Dictionary<string, BatteryDevice>();
        static readonly HashSet<string> warned = new HashSet<string>();
        static readonly Dictionary<string, XentaListener> xenta = new Dictionary<string, XentaListener>();
        static Thread worker;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        public static int LowAt = 20;
        public static bool Debug; // /batlog: registra cada leitura em turzx.log

        public static void Start()
        {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "baterias", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public static void Refresh() { wake.Set(); }

        public static BatteryDevice Lowest(string kind)
        {
            BatteryDevice best = null;
            foreach (var d in List)
                if ((kind == null || d.Kind == kind) && d.Percent >= 0 && (best == null || d.Percent < best.Percent)) best = d;
            if (best == null) foreach (var d in List) if (kind == null || d.Kind == kind) { best = d; break; }
            return best;
        }

        public static string KindName(string k)
        {
            switch (k)
            {
                case "mouse": return "Mouse";
                case "keyboard": return "Teclado";
                case "headset": return "Fone";
                case "gamepad": return "Controle";
                default: return "Aparelho";
            }
        }

        static void Loop()
        {
            Thread.Sleep(3000);
            while (true)
            {
                if (Enabled)
                {
                    try { Poll(); } catch (Exception ex) { Log.Error("baterias", ex); }
                }
                wake.WaitOne(15000); // rapido o bastante para o cartao sumir logo ao desconectar
            }
        }

        static void Poll()
        {
            var found = new List<BatteryDevice>();
            // aparelhos ainda conectados mas sem leitura nova nesta rodada
            var alive = new HashSet<string>();
            Dictionary<string, BtNode> bt = null;
            try { bt = BluetoothNodes(); } catch (Exception ex) { Log.Error("bateria bluetooth", ex); }
            if (bt != null) try { PollBluetooth(bt, found); } catch (Exception ex) { Log.Error("bateria bluetooth", ex); }
            List<string> hid = null;
            try { hid = HidPaths(); } catch (Exception ex) { Log.Error("bateria hid", ex); }
            if (hid != null)
            {
                try { PollPlayStation(hid, bt, found, alive); } catch (Exception ex) { Log.Error("bateria dualsense", ex); }
                try { PollXenta(hid, found, alive); } catch (Exception ex) { Log.Error("bateria xenta", ex); }
            }
            try { PollXInput(found); } catch (DllNotFoundException) { } catch (Exception ex) { Log.Error("bateria xinput", ex); }

            var now = DateTime.Now;
            foreach (var d in found) { d.Seen = now; keep[d.Key] = d; }
            // desconectou: sai na hora. Conectado mas sem leitura (mouse parado): fica o ultimo valor por 30 min
            var list = new List<BatteryDevice>();
            foreach (var k in new List<string>(keep.Keys))
            {
                var d = keep[k];
                bool fresh = found.Contains(d);
                if (!fresh && (!alive.Contains(k) || (now - d.Seen).TotalMinutes > 30)) { keep.Remove(k); warned.Remove(k); continue; }
                if (!fresh && d.Kind == "mouse") d.Asleep = true;
                list.Add(d);
            }
            list.Sort((a, b) => string.Compare(a.Kind + a.Name, b.Kind + b.Name, StringComparison.Ordinal));
            List = list;
            if (Debug)
            {
                var sb = new StringBuilder();
                foreach (var d in list) sb.Append(d.Kind + "=" + d.Name + " " + d.Percent + "%" + (d.Asleep ? " (dormindo)" : "") + "; ");
                Log.Write("baterias", sb.Length > 0 ? sb.ToString() : "nenhuma");
            }

            foreach (var d in list)
            {
                if (d.Percent < 0 || d.Asleep) continue;
                if (d.Charging || d.Percent > LowAt + 10) { warned.Remove(d.Key); continue; }
                if (d.Percent <= LowAt && warned.Add(d.Key))
                {
                    var h = Low; if (h != null) try { h(d); } catch (Exception ex) { Log.Error("bateria aviso", ex); }
                }
            }
        }

        // ---------------- Bluetooth (valor do Windows)
        class BtNode { public int Battery = -1; public int Connected = -1; public int Cod = -1; public string Name = ""; public string Ids = ""; }

        static void PollBluetooth(Dictionary<string, BtNode> nodes, List<BatteryDevice> found)
        {
            foreach (var kv in nodes)
            {
                var n = kv.Value;
                // so aparelhos conectados agora: o Windows guarda o ultimo nivel de quem ja desconectou
                if (n.Battery < 0 || n.Connected == 0 || n.Name == "") continue;
                int major = n.Cod >= 0 ? (n.Cod >> 8) & 0x1F : -1, minor = n.Cod >= 0 ? (n.Cod >> 2) & 0x3F : -1;
                found.Add(new BatteryDevice { Key = "bt:" + kv.Key, Name = n.Name, Kind = BtKind(major, minor, n.Name), Via = "Bluetooth", Percent = Math.Max(0, Math.Min(100, n.Battery)) });
            }
        }

        static string BtKind(int major, int minor, string name)
        {
            if (major == 4) return "headset";
            if (major == 5 && minor >= 0)
            {
                if ((minor & 0x0F) == 1 || (minor & 0x0F) == 2) return "gamepad";
                if ((minor & 0x30) == 0x20) return "mouse";
                if ((minor & 0x30) == 0x10 || (minor & 0x30) == 0x30) return "keyboard";
            }
            return GuessKind(name);
        }

        public static string GuessKind(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, "mouse|mx master|mx anywhere|trackball")) return "mouse";
            if (Regex.IsMatch(n, "keyboard|teclado|keys|kbd")) return "keyboard";
            if (Regex.IsMatch(n, "controller|gamepad|dualsense|dualshock|joy-con|controle")) return "gamepad";
            if (Regex.IsMatch(n, "buds|head|phone|fone|airpods|ear|audio|wh-|wf-|jbl|edifier|beats")) return "headset";
            return "other";
        }

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr g, string e, IntPtr w, int f);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr s, ref SP_DEVINFO_DATA d, StringBuilder b, int sz, out int req);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDevicePropertyW(IntPtr s, ref SP_DEVINFO_DATA d, ref DEVPROPKEY k, out uint type, byte[] b, int sz, out int req, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        static readonly Regex BtAddr = new Regex(@"(?:DEV_|&)([0-9A-F]{12})(?:_C|\\|$)");
        static readonly Regex BtVidPid = new Regex(@"VID&0002([0-9A-F]{4})_PID&([0-9A-F]{4})");

        // o Windows mantem o no HID de um controle Bluetooth mesmo desconectado: vale o "conectado" do aparelho
        static bool BtDisconnected(Dictionary<string, BtNode> nodes, int vid, int pid)
        {
            if (nodes == null) return false;
            string want = vid.ToString("X4") + ":" + pid.ToString("X4") + ";";
            bool any = false;
            foreach (var n in nodes.Values)
            {
                if (n.Ids.IndexOf(want, StringComparison.Ordinal) < 0) continue;
                if (n.Connected != 0) return false;
                any = true;
            }
            return any;
        }

        static readonly Guid BatteryKey = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");   // pid 2, nivel da bateria
        static readonly Guid ConnectedKey = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"); // pid 15, conectado agora
        static readonly Guid CodKey = new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A");       // pid 10, classe do aparelho
        static readonly Guid NameKey = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");      // pid 14, nome

        static bool Prop(IntPtr set, ref SP_DEVINFO_DATA d, Guid g, int pid, byte[] buf, out int len)
        {
            var key = new DEVPROPKEY { fmtid = g, pid = pid };
            uint type;
            if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, buf.Length, out len, 0)) { len = 0; return false; }
            return len > 0;
        }

        // nos Bluetooth presentes, agrupados por endereco do aparelho
        static Dictionary<string, BtNode> BluetoothNodes()
        {
            var r = new Dictionary<string, BtNode>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4); // PRESENT | ALLCLASSES
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return r;
            try
            {
                var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
                var sb = new StringBuilder(512); var buf = new byte[512];
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    int req;
                    if (!SetupDiGetDeviceInstanceId(set, ref d, sb, sb.Capacity, out req)) continue;
                    string id = sb.ToString().ToUpperInvariant();
                    if (!id.StartsWith("BTH")) continue;
                    var m = BtAddr.Match(id);
                    if (!m.Success) continue;
                    BtNode n;
                    if (!r.TryGetValue(m.Groups[1].Value, out n)) r[m.Groups[1].Value] = n = new BtNode();
                    int len;
                    var vp = BtVidPid.Match(id);
                    if (vp.Success) n.Ids += vp.Groups[1].Value + ":" + vp.Groups[2].Value + ";";
                    if (Prop(set, ref d, BatteryKey, 2, buf, out len)) n.Battery = buf[0];
                    bool main = id.StartsWith("BTHENUM\\DEV_") || id.StartsWith("BTHLE\\DEV_");
                    if (!main) continue;
                    if (Prop(set, ref d, ConnectedKey, 15, buf, out len)) n.Connected = buf[0] != 0 ? 1 : 0;
                    if (Prop(set, ref d, CodKey, 10, buf, out len) && len >= 4) n.Cod = BitConverter.ToInt32(buf, 0);
                    if (Prop(set, ref d, NameKey, 14, buf, out len) && len >= 2) n.Name = Encoding.Unicode.GetString(buf, 0, len).TrimEnd('\0');
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return r;
        }

        // ---------------- HID
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, Version; }
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int f; public IntPtr r; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort a, b, c, d, e, f, g, h, i, j;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA o);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr o, int sz, out int req, IntPtr di);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

        static List<string> HidPaths()
        {
            Guid g; HidD_GetHidGuid(out g);
            var paths = new List<string>();
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;
            try
            {
                var di = new SP_DEVICE_INTERFACE_DATA(); di.cbSize = Marshal.SizeOf(di);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref di); i++)
                {
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref di, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref di, buf, req, out req, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        static readonly Regex VidPid = new Regex(@"vid[_&](?:0002)?([0-9a-f]{4})[&_]pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);

        static bool Ids(string path, out int vid, out int pid)
        {
            vid = pid = 0;
            var m = VidPid.Match(path);
            if (!m.Success) return false;
            vid = Convert.ToInt32(m.Groups[1].Value, 16); pid = Convert.ToInt32(m.Groups[2].Value, 16);
            return true;
        }

        // abre so para leitura, assincrono (para poder desistir de esperar)
        static FileStream OpenRead(string path, out HIDP_CAPS caps)
        {
            caps = new HIDP_CAPS();
            var h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) return null;
            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) { h.Dispose(); return null; }
            HidP_GetCaps(pp, out caps); HidD_FreePreparsedData(pp);
            if (caps.InLen == 0) { h.Dispose(); return null; }
            return new FileStream(h, FileAccess.Read, caps.InLen, true);
        }

        static int ReadReport(FileStream fs, byte[] buf, int timeoutMs)
        {
            var ar = fs.BeginRead(buf, 0, buf.Length, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return -1; // quem chama fecha o arquivo, o que cancela a leitura
            return fs.EndRead(ar);
        }

        // ---------------- DualSense / DualShock 4
        static void PollPlayStation(List<string> paths, Dictionary<string, BtNode> btNodes, List<BatteryDevice> found, HashSet<string> alive)
        {
            foreach (string path in paths)
            {
                int vid, pid;
                if (!Ids(path, out vid, out pid) || vid != 0x054C) continue;
                bool ds4 = pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0, ds = pid == 0x0CE6 || pid == 0x0DF2;
                if (!ds4 && !ds) continue;
                HIDP_CAPS caps;
                FileStream fs = null;
                try
                {
                    fs = OpenRead(path, out caps);
                    if (fs == null || caps.UsagePage != 1 || caps.Usage != 5) continue;
                    bool bt = caps.InLen > 64;
                    if (bt && BtDisconnected(btNodes, vid, pid)) continue;
                    string key = "ps:" + pid.ToString("X4") + ":" + (path.GetHashCode() & 0xFFFFFF).ToString("X");
                    alive.Add(key);
                    if (bt)
                    {
                        // ler o relatorio de calibracao faz o controle mandar o relatorio completo, como os jogos fazem
                        var f = new byte[Math.Max(64, (int)caps.FeatLen)]; f[0] = 0x05;
                        HidD_GetFeature(fs.SafeFileHandle, f, caps.FeatLen > 0 ? caps.FeatLen : 41);
                    }
                    var r = new byte[caps.InLen];
                    var end = DateTime.Now.AddMilliseconds(1200);
                    while (DateTime.Now < end)
                    {
                        int n = ReadReport(fs, r, 600);
                        if (n < 0) break;
                        if (n < 10) continue;
                        int at;
                        if (ds4) at = r[0] == 0x01 && !bt ? 30 : r[0] == 0x11 ? 32 : 0;
                        else at = r[0] == 0x01 && !bt ? 53 : r[0] == 0x31 ? 54 : 0;
                        if (at == 0 || n <= at) continue;
                        int level = r[at] & 0x0F, state = r[at] >> 4;
                        var d = new BatteryDevice { Kind = "gamepad", Via = bt ? "Bluetooth" : "USB" };
                        d.Name = ds4 ? "DualShock 4" : pid == 0x0DF2 ? "DualSense Edge" : "DualSense";
                        d.Key = key;
                        if (ds4)
                        {
                            bool cable = (r[at] & 0x10) != 0;
                            d.Percent = cable ? (level >= 11 ? 100 : Math.Min(100, level * 10)) : Math.Min(100, level * 10 + 5);
                            d.Charging = cable && level < 11;
                        }
                        else
                        {
                            d.Percent = Math.Min(100, level * 10 + 5);
                            d.Charging = state == 1;
                            if (state == 2) { d.Percent = 100; d.Charging = true; }
                        }
                        found.Add(d);
                        break;
                    }
                }
                catch (IOException) { }
                finally { if (fs != null) fs.Dispose(); }
            }
        }

        // ---------------- receptores Xenta (1D57): escuta o anuncio [03][modelo][evento][estado][nivel]
        class XentaListener
        {
            public string Path; public int Pid;
            public byte[] Last, Prev; public DateTime LastAt;
            public volatile bool Dead;
        }

        static readonly object xlock = new object();

        static string XentaModel(byte code, out bool tens)
        {
            tens = false;
            switch (code)
            {
                case 0x73: return "Inphic IN10R MAX";
                case 0x74: return "Inphic IN10 MAX";
                case 0x3D: case 0x30: case 0x50: return "Delux M900 Pro";
                case 0x55: return "Attack Shark X11";
                case 0x4E: return "Attack Shark X3 Max";
                case 0xE5: return "Attack Shark V3 Pro";
                case 0x24: return "Attack Shark V6";
                case 0x10: tens = true; return "Attack Shark X6 / R1";
                default: return null;
            }
        }

        static void PollXenta(List<string> paths, List<BatteryDevice> found, HashSet<string> alive)
        {
            var present = new HashSet<string>();
            foreach (string path in paths)
            {
                int vid, pid;
                if (!Ids(path, out vid, out pid) || vid != 0x1D57) continue;
                if (pid != 0xFA60 && pid != 0xFA65 && pid != 0xFA55 && pid != 0x2155) continue;
                present.Add(path);
                // receptor ligado: o mouse so fica quieto quando parado, entao o ultimo valor continua
                alive.Add("xenta:" + pid.ToString("X4"));
                XentaListener l;
                lock (xlock) xenta.TryGetValue(path, out l);
                if (l == null || l.Dead)
                {
                    HIDP_CAPS caps;
                    FileStream fs;
                    try { fs = OpenRead(path, out caps); } catch { fs = null; caps = new HIDP_CAPS(); }
                    if (fs == null) continue;
                    if (caps.UsagePage != 0x000A || caps.InLen < 5) { fs.Dispose(); continue; }
                    l = new XentaListener { Path = path, Pid = pid };
                    lock (xlock) xenta[path] = l;
                    var lis = l;
                    new Thread(() => XentaRead(lis, fs)) { IsBackground = true, Name = "xenta" }.Start();
                    Thread.Sleep(5000); // espera dois anuncios na primeira vez
                }
                BatteryDevice d = XentaDecode(l);
                if (d != null) found.Add(d);
            }
            lock (xlock)
                foreach (var k in new List<string>(xenta.Keys))
                    if (!present.Contains(k)) { xenta[k].Dead = true; xenta.Remove(k); }
        }

        static void XentaRead(XentaListener l, FileStream fs)
        {
            try
            {
                var buf = new byte[64];
                while (!l.Dead)
                {
                    int n;
                    try { n = fs.Read(buf, 0, buf.Length); } catch { break; }
                    if (n < 5 || buf[0] != 0x03 || (buf[2] != 0x40 && buf[2] != 0x41)) continue;
                    var frame = new byte[5]; Array.Copy(buf, frame, 5);
                    lock (xlock) { l.Prev = l.Last; l.Last = frame; l.LastAt = DateTime.Now; }
                }
            }
            catch { }
            finally { l.Dead = true; try { fs.Dispose(); } catch { } }
        }

        static BatteryDevice XentaDecode(XentaListener l)
        {
            byte[] last, prev; DateTime at;
            lock (xlock) { last = l.Last; prev = l.Prev; at = l.LastAt; }
            if (last == null || (DateTime.Now - at).TotalSeconds > 15) return null; // dormindo: fica o ultimo valor
            bool cable = l.Pid == 0xFA55 || l.Pid == 0x2155, tens;
            byte code = last[1], st = last[3], level = last[4];
            string name = XentaModel(code, out tens);
            var d = new BatteryDevice { Key = "xenta:" + l.Pid.ToString("X4"), Name = name ?? "Mouse sem fio", Kind = "mouse", Via = cable ? "cabo USB" : "receptor 2.4G" };
            bool agreed = prev != null && prev[1] == last[1] && prev[2] == last[2] && prev[3] == last[3] && prev[4] == last[4];
            bool delux = l.Pid == 0xFA65 && (code == 0x30 || code == 0x50);
            int ardor = l.Pid == 0xFA60 && !cable && code == 0x00 && last[2] == 0x40 && (st & 0x0F) == 0x03
                ? ((st >> 4) == 1 ? 25 : (st >> 4) == 3 ? 50 : (st >> 4) == 7 ? 75 : (st >> 4) == 0xF ? 100 : -1) : -1;
            if (delux)
            {
                if (st == 2) d.Percent = 100;
                else if (st == 3) { d.Charging = true; if (level <= 100) d.Percent = level; }
                else if (agreed && st == 1 && level >= 1 && level <= 100) d.Percent = level;
            }
            else if (ardor >= 0) { d.Name = "ARDOR GAMING Fury V2"; if (agreed) d.Percent = ardor; }
            else if (cable || st == 2 || st == 3) { d.Charging = true; if (st == 2 && !cable) d.Percent = 100; }
            else if (agreed)
            {
                if (tens) { if (level >= 1 && level <= 10) d.Percent = level * 10; }
                else if (st == 1 && level >= 1 && level <= 100) d.Percent = level;
            }
            if (d.Percent < 0 && !d.Charging) return null;
            return d;
        }

        // ---------------- controles Xbox
        [StructLayout(LayoutKind.Sequential)] struct XINPUT_BATTERY_INFORMATION { public byte Type, Level; }
        [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int user, byte devType, out XINPUT_BATTERY_INFORMATION b);

        static void PollXInput(List<BatteryDevice> found)
        {
            for (int i = 0; i < 4; i++)
            {
                XINPUT_BATTERY_INFORMATION b;
                if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;
                if (b.Type != 2 && b.Type != 3) continue; // 0 desconectado, 1 com fio (inclui os virtuais), FF desconhecido
                int pct = b.Level == 0 ? 5 : b.Level == 1 ? 25 : b.Level == 2 ? 60 : 100;
                found.Add(new BatteryDevice { Key = "xinput:" + i, Name = "Controle Xbox " + (i + 1), Kind = "gamepad", Via = "sem fio", Percent = pct });
            }
        }
    }
}
