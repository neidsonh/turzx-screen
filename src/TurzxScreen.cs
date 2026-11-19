// TurzxScreen - controle simples para telas Turing / TURZX 3.5" (rev. A, USB 1A86:5722)
// Protocolo baseado em github.com/mathoudebine/turing-smart-screen-python (lcd_comm_rev_a.py)
// Compilar: build.bat (usa o csc do .NET Framework que ja vem no Windows)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TurzxScreen
{
    // ------------------------------------------------------------------ LCD
    class Lcd
    {
        SerialPort sp;
        byte[] raw, cur, prev; // buffers reaproveitados entre quadros
        bool prevValid;
        int lastW, lastH;
        public int Orientation = 2; // 0 retrato, 1 retrato inv., 2 paisagem, 3 paisagem inv.
        public long BytesSent;

        public bool IsOpen { get { return sp != null && sp.IsOpen; } }
        public int Width { get { return Orientation >= 2 ? 480 : 320; } }
        public int Height { get { return Orientation >= 2 ? 320 : 480; } }

        public static string FindPort()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID[_]1A86&PID[_]5722%'"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        var m = Regex.Match(Convert.ToString(mo["Name"]), @"\((COM\d+)\)");
                        if (m.Success) return m.Groups[1].Value;
                    }
                }
            }
            catch { }
            return null;
        }

        public void Open(string port)
        {
            Close();
            sp = new SerialPort(port, 115200);
            sp.Handshake = Handshake.RequestToSend;
            sp.WriteTimeout = 5000;
            sp.ReadTimeout = 1000;
            sp.WriteBufferSize = 1 << 20;
            sp.Open();
            prevValid = false;
        }

        public void Close()
        {
            try { if (sp != null) sp.Close(); } catch { }
            sp = null;
        }

        void Cmd(int x, int y, int ex, int ey, byte cmd)
        {
            var b = new byte[6];
            b[0] = (byte)(x >> 2);
            b[1] = (byte)(((x & 3) << 6) + (y >> 4));
            b[2] = (byte)(((y & 15) << 4) + (ex >> 6));
            b[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
            b[4] = (byte)(ey & 255);
            b[5] = cmd;
            sp.Write(b, 0, 6);
        }

        public void SetBrightness(int pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            Cmd(255 - pct * 255 / 100, 0, 0, 0, 110);
        }

        public void ScreenOn(bool on) { Cmd(0, 0, 0, 0, (byte)(on ? 109 : 108)); }

        public void SetOrientation(int o)
        {
            Orientation = o;
            var b = new byte[16];
            b[5] = 121;
            b[6] = (byte)(o + 100);
            b[7] = (byte)(Width >> 8); b[8] = (byte)(Width & 255);
            b[9] = (byte)(Height >> 8); b[10] = (byte)(Height & 255);
            sp.Write(b, 0, 16);
            prevValid = false;
        }

        // pode ser chamado de outra thread: so marca, e o proximo Push envia o quadro inteiro
        volatile bool invalid;
        public void Invalidate() { invalid = true; }

        void ToRgb565(Bitmap bmp, byte[] dst)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = data.Stride, size = stride * h;
            if (raw == null || raw.Length != size) raw = new byte[size];
            Marshal.Copy(data.Scan0, raw, 0, size);
            bmp.UnlockBits(data);
            var src = raw;
            int d = 0;
            for (int y = 0; y < h; y++)
            {
                int s = y * stride;
                for (int x = 0; x < w; x++, s += 3)
                {
                    int v = ((src[s + 2] & 0xF8) << 8) | ((src[s + 1] & 0xFC) << 3) | (src[s] >> 3);
                    dst[d++] = (byte)v;
                    dst[d++] = (byte)(v >> 8);
                }
            }
        }

        // Envia so as areas que mudaram (a tela e lenta: ~170 KB/s)
        public void Push(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, size = w * h * 2;
            if (cur == null || cur.Length != size || lastW != w || lastH != h)
            {
                cur = new byte[size]; prev = new byte[size]; prevValid = false;
                lastW = w; lastH = h;
            }
            ToRgb565(bmp, cur);
            if (invalid) { invalid = false; prevValid = false; }
            int tr = Transition;
            if (!prevValid)
            {
                if (tr == 0) SendRect(cur, w, 0, 0, w - 1, h - 1);
                else SendTransition(cur, null, w, h, tr);
            }
            else
            {
                var rects = DirtyRects.Find(cur, prev, w, h);
                long area = 0; foreach (var r in rects) area += (long)r.Width * r.Height;
                // troca de tela (muda quase tudo): envia na ordem da transicao escolhida
                if (tr != 0 && area > (long)w * h * 45 / 100) SendTransition(cur, prev, w, h, tr);
                else
                    foreach (var r in rects)
                        SendRect(cur, w, r.Left, r.Top, r.Right - 1, r.Bottom - 1);
            }
            var old = prev; prev = cur; cur = old; prevValid = true;
        }

        // 0 cortina (de cima para baixo, o normal), 1 mosaico, 2 do centro para fora, 3 da esquerda
        // para a direita, 4 persiana. O tempo total e o mesmo (limite da tela); muda so a ordem.
        public volatile int Transition;
        public static readonly string[] TransitionNames = { "Cortina (de cima para baixo)", "Mosaico", "Do centro para fora", "Da esquerda para a direita", "Persiana" };
        static readonly Random rnd = new Random();

        void SendTransition(byte[] px, byte[] old, int w, int h, int tr)
        {
            if (tr == 4)
            {
                // faixas de 16 linhas intercaladas: 1a de cada 4, depois a 2a...
                const int band = 16;
                for (int pass = 0; pass < 4; pass++)
                    for (int y = pass * band; y < h; y += band * 4)
                    {
                        int y1 = Math.Min(h, y + band) - 1;
                        if (old == null || !SameArea(px, old, w, 0, y, w - 1, y1)) SendRect(px, w, 0, y, w - 1, y1);
                    }
                return;
            }
            const int t = 32;
            var tiles = new List<Rectangle>();
            for (int y = 0; y < h; y += t)
                for (int x = 0; x < w; x += t)
                {
                    var r = new Rectangle(x, y, Math.Min(t, w - x), Math.Min(t, h - y));
                    if (old == null || !SameArea(px, old, w, r.X, r.Y, r.Right - 1, r.Bottom - 1)) tiles.Add(r);
                }
            if (tr == 1)
                lock (rnd) for (int i = tiles.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); var s = tiles[i]; tiles[i] = tiles[j]; tiles[j] = s; }
            else if (tr == 2)
            {
                double cx = w / 2.0, cy = h / 2.0;
                tiles.Sort((a, b) => Dist(a, cx, cy).CompareTo(Dist(b, cx, cy)));
            }
            else if (tr == 3) tiles.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            foreach (var r in tiles) SendRect(px, w, r.X, r.Y, r.Right - 1, r.Bottom - 1);
        }

        static double Dist(Rectangle r, double cx, double cy)
        {
            double dx = r.X + r.Width / 2.0 - cx, dy = r.Y + r.Height / 2.0 - cy;
            return dx * dx + dy * dy;
        }

        static bool SameArea(byte[] a, byte[] b, int w, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                int i = (y * w + x0) * 2, e = (y * w + x1) * 2 + 1;
                for (; i <= e; i++) if (a[i] != b[i]) return false;
            }
            return true;
        }

        byte[] sendBuf = new byte[0];

        void SendRect(byte[] px, int w, int x0, int y0, int x1, int y1)
        {
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1, size = rw * rh * 2;
            Cmd(x0, y0, x1, y1, 197);
            if (rw == w) sp.Write(px, y0 * w * 2, size);
            else
            {
                if (sendBuf.Length < size) sendBuf = new byte[size];
                for (int r = 0; r < rh; r++)
                    Buffer.BlockCopy(px, ((y0 + r) * w + x0) * 2, sendBuf, r * rw * 2, rw * 2);
                sp.Write(sendBuf, 0, size);
            }
            BytesSent += size;
        }
    }

    // ------------------------------------------------------------------ Sistema
    static class SysInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint Low, High; public long V { get { return ((long)High << 32) | Low; } } }
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        class MEMSTATUS
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMSTATUS));
            public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMSTATUS m);

        static long pIdle, pTotal, pRx, pTx; static DateTime pNet = DateTime.MinValue;
        public static double Cpu, RamPct, RamUsedGb, RamTotalGb, NetDown, NetUp, DiskPct;
        public static double GpuPct = -1, GpuTemp = -1, DiskFreeGb, GpuPowerSmi = -1;
        public static DateTime NeedGpuPowerUntil = DateTime.MinValue;
        public static volatile bool GpuPowerUnsupported;
        static volatile bool gpuInfoRead;
        static string nvsmi;
        static DateTime lastGpu = DateTime.MinValue;

        static bool updated; static int lastUpdate, lastDisk;

        // no maximo cerca de 1 vez por segundo, mesmo com espelho, GIF ou notificacao na tela
        public static void Update(bool screenOff = false)
        {
            int tick = Environment.TickCount;
            bool first = !updated;
            if (!first && unchecked(tick - lastUpdate) < 900) return;
            updated = true; lastUpdate = tick;
            FILETIME i, k, u;
            if (GetSystemTimes(out i, out k, out u))
            {
                long idle = i.V, total = k.V + u.V;
                if (pTotal != 0 && total != pTotal) Cpu = Math.Max(0, Math.Min(100, 100.0 * (1 - (double)(idle - pIdle) / (total - pTotal))));
                pIdle = idle; pTotal = total;
            }
            var m = new MEMSTATUS();
            if (GlobalMemoryStatusEx(m))
            {
                RamPct = m.dwMemoryLoad;
                RamTotalGb = m.ullTotalPhys / 1073741824.0;
                RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            }
            try
            {
                long rx, tx; bool nicsChanged;
                NetStats.Read(out rx, out tx, out nicsChanged);
                var now = DateTime.Now;
                if (pNet != DateTime.MinValue && !nicsChanged)
                {
                    double s = (now - pNet).TotalSeconds;
                    if (s > 0) { NetDown = Math.Max(0, (rx - pRx) / s); NetUp = Math.Max(0, (tx - pTx) / s); }
                }
                pRx = rx; pTx = tx; pNet = now;
            }
            catch { }
            if (first || unchecked(tick - lastDisk) >= 30000)
            {
                lastDisk = tick;
                try
                {
                    var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                    DiskPct = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                    DiskFreeGb = d.TotalFreeSpace / 1e9;
                }
                catch { }
            }
            // com a tela apagada ninguem ve temperatura nem GPU: nao consulta os sensores
            if (!screenOff)
            {
                CpuTemp.Poll();
                if (Lhm.Available && Lhm.GpuLoad >= 0) GpuPct = Lhm.GpuLoad;
                if (Lhm.Available && Lhm.GpuTemp >= 0) GpuTemp = Lhm.GpuTemp;
                // nvidia-smi: 1 vez para nome/memoria; depois so sem LibreHardwareMonitor ou para o consumo da GPU
                bool needSmi = !gpuInfoRead || !Lhm.Available || Lhm.GpuLoad < 0 || (DateTime.Now < NeedGpuPowerUntil && Lhm.GpuPower < 0 && !GpuPowerUnsupported);
                if (needSmi && (DateTime.Now - lastGpu).TotalSeconds >= 2) { lastGpu = DateTime.Now; ThreadPool.QueueUserWorkItem(delegate { ReadGpu(); }); }
            }
            if ((DateTime.Now - lastSample).TotalMilliseconds >= 950)
            {
                lastSample = DateTime.Now;
                int hi = HCount % HLen;
                HCpu[hi] = (float)Cpu; HRam[hi] = (float)RamPct; HGpu[hi] = (float)Math.Max(0, GpuPct);
                HCount++;
            }
        }

        // historico (1 amostra/s)
        public const int HLen = 512;
        public static float[] HCpu = new float[HLen], HRam = new float[HLen], HGpu = new float[HLen];
        public static int HCount;
        static DateTime lastSample = DateTime.MinValue;

        // NVML: a mesma biblioteca que o nvidia-smi usa, sem abrir um processo a cada leitura
        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint Gpu, Mem; }
        [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong Total, Free, Used; }
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvUtil u);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out NvMem m);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr dev, System.Text.StringBuilder name, uint len);
        static int nvmlState; // 0 nao tentado, 1 ok, -1 indisponivel
        static IntPtr nvDev;
        static readonly object gpuLock = new object();

        static bool ReadNvml()
        {
            try
            {
                if (nvmlState == 0)
                {
                    nvmlState = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out nvDev) == 0 ? 1 : -1;
                    if (nvmlState < 0) return false;
                }
                if (nvmlState < 0) return false;
                NvUtil u; uint t;
                if (nvmlDeviceGetUtilizationRates(nvDev, out u) != 0 || nvmlDeviceGetTemperature(nvDev, 0, out t) != 0) return false;
                GpuPct = u.Gpu; GpuTemp = t;
                uint mw;
                if (nvmlDeviceGetPowerUsage(nvDev, out mw) == 0) GpuPowerSmi = mw / 1000.0;
                else { GpuPowerSmi = -1; GpuPowerUnsupported = true; }
                if (!gpuInfoRead)
                {
                    var sb = new System.Text.StringBuilder(96); NvMem m;
                    if (nvmlDeviceGetName(nvDev, sb, (uint)sb.Capacity) == 0) Specs.Gpu = Specs.ShortGpu(sb.ToString().Trim());
                    if (nvmlDeviceGetMemoryInfo(nvDev, out m) == 0) Specs.GpuMem = Math.Round(m.Total / 1073741824.0) + " GB";
                    gpuInfoRead = true;
                }
                return true;
            }
            catch (DllNotFoundException) { nvmlState = -1; return false; }
            catch (EntryPointNotFoundException) { nvmlState = -1; return false; }
            catch { return false; }
        }

        static void ReadGpu()
        {
            lock (gpuLock)
            {
                if (ReadNvml()) return;
                ReadGpuSmi();
            }
        }

        static void ReadGpuSmi()
        {
            try
            {
                if (nvsmi == null)
                {
                    string[] c = { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                                   @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" };
                    nvsmi = "";
                    foreach (var p in c) if (File.Exists(p)) { nvsmi = p; break; }
                }
                if (nvsmi == "") return;
                var psi = new ProcessStartInfo(nvsmi, "--query-gpu=utilization.gpu,temperature.gpu,name,memory.total,power.draw --format=csv,noheader,nounits");
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadLine(); p.WaitForExit(3000);
                    var parts = o.Split(',');
                    GpuPct = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                    GpuTemp = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                    if (parts.Length >= 4)
                    {
                        Specs.Gpu = Specs.ShortGpu(parts[2].Trim());
                        double mib; if (double.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mib)) Specs.GpuMem = Math.Round(mib / 1024) + " GB";
                        gpuInfoRead = true;
                    }
                    double watts;
                    GpuPowerSmi = parts.Length >= 5 && double.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out watts) ? watts : -1;
                    if (parts.Length >= 5 && GpuPowerSmi < 0) GpuPowerUnsupported = true; // driver responde [N/A]
                }
            }
            catch { }
        }

        public static string Rate(double bps)
        {
            if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB/s";
            if (bps >= 1024) return (bps / 1024).ToString("0") + " KB/s";
            return bps.ToString("0") + " B/s";
        }
    }

    // ------------------------------------------------------------------ Config
    class Settings
    {
        public Dictionary<string, string> D = new Dictionary<string, string>();
        static string FilePath { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "turzx.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try { foreach (var l in File.ReadAllLines(FilePath)) { int i = l.IndexOf('='); if (i > 0) s.D[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
            return s;
        }
        string lastSaved;

        // grava so se mudou, em arquivo temporario: uma queda de energia nao corrompe o .ini
        public void Save()
        {
            var lines = new List<string>();
            foreach (var kv in D) lines.Add(kv.Key + "=" + kv.Value);
            string text = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            if (text == lastSaved) return;
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, text);
                LayoutStore.ReplaceFile(tmp, FilePath);
                lastSaved = text;
            }
            catch (Exception ex) { Log.Error("config", ex); }
        }
        public string Get(string k, string def) { string v; return D.TryGetValue(k, out v) ? v : def; }
        public int GetInt(string k, int def) { int v; return int.TryParse(Get(k, ""), out v) ? v : def; }
        public bool GetBool(string k, bool def) { return Get(k, def ? "1" : "0") == "1"; }
        public Color GetColor(string k, Color def) { try { return ColorTranslator.FromHtml(Get(k, ColorTranslator.ToHtml(def))); } catch { return def; } }
        public void Set(string k, object v) { D[k] = v is bool ? ((bool)v ? "1" : "0") : v is Color ? ColorTranslator.ToHtml((Color)v) : Convert.ToString(v); }
    }

    // ------------------------------------------------------------------ Form
    partial class MainForm : Form
    {
        Lcd lcd = new Lcd();
        Settings cfg = Settings.Load();
        Thread worker;
        volatile bool running = true;
        volatile bool needConfig = true;
        object imgLock = new object();

        // estado lido pela thread de render
        volatile int mode; // 0 painel, 1 imagem, 2 espelho, 3 texto
        Color bgColor, accent, fgColor;
        string title = "", bgImagePath = "", imagePath = "", textContent = "";
        bool imageFill, mirrorFollowMouse, clock24 = true;
        int brightness, orientation, mirrorScreen, mirrorFps, textSize;
        Image bgImage, image;
        FrameDimension gifDim; int gifFrames, gifFrame; int[] gifDelays;

        // UI
        PictureBox preview; Label status; TrackBar trkBright; ComboBox cboOrient; TabControl tabs;
        TextBox txtTitle, txtText; Label lblImage, lblBg; CheckBox chkFill, chkFollow, chkStartup, chk24;
        ComboBox cboScreen; NumericUpDown numFps, numTextSize;
        Button btnBg, btnAccent, btnFg;
        NotifyIcon tray;

        // notificacoes
        bool notifEnabled, notifHideBody;
        volatile int dashTheme; ComboBox cboTheme;
        bool musicToast; CheckBox chkMusicToast; int seenTrack = -1;
        int notifSeconds;
        string notifIgnore = "";
        ToastInfo curToast; DateTime toastUntil;
        CheckBox chkNotif, chkNotifHide; NumericUpDown numNotifSec; TextBox txtNotifIgnore; Label lblNotifStatus;

        public MainForm()
        {
            Text = "TURZX Screen"; Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(512, 714); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; // LayoutWindow ajusta a largura pelas abas
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            LoadSettings();
            BuildUi();
            RgbTick();
            tray = new NotifyIcon(); tray.Icon = Icon; tray.Text = "TURZX Screen"; tray.Visible = true;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            var menu = new ContextMenu();
            menu.MenuItems.Add("Abrir", delegate { Show(); WindowState = FormWindowState.Normal; });
            menu.MenuItems.Add("Sair", delegate { Close(); });
            tray.ContextMenu = menu;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            HandleCreated += delegate { formHandle = Handle; };
            if (IsHandleCreated) formHandle = Handle;
            OnAir.Start();
            Gaming.Start();
            Agenda.Start();
            PhotoFrame.Start();
            ApiServer.GetStatus = delegate
            {
                int count; var top = StatusBoard.Top(out count);
                return new Dictionary<string, object> {
                    { "connected", lcd.IsOpen }, { "width", lcd.Width }, { "height", lcd.Height }, { "onAir", OnAir.Active },
                    { "statusCount", count }, { "statusLabel", top != null ? top.Label : "" },
                    { "statusState", top != null ? top.State : "" }, { "statusText", top != null ? top.Text : "" } };
            };
            if (apiEnabled) ApiServer.Start();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            NotificationWatcher.Start();
            Batteries.Low += d =>
            {
                if (!batWarn) return;
                NotificationWatcher.Inject(new ToastInfo { App = "Bateria", Title = d.Name + ": " + d.Percent + "%", Body = Batteries.KindName(d.Kind) + " com bateria fraca, hora de carregar.", Time = DateTime.Now, Direct = true, Seconds = 12 });
            };
            Batteries.Start();
            Vpn.Changed += (c, up) =>
            {
                if (!vpnToast) return;
                NotificationWatcher.Inject(new ToastInfo { App = "VPN", Title = (up ? "Conectada: " : "Desconectada: ") + Vpn.Place(c, vpnHide), Body = Vpn.Program(c, vpnHide), Time = DateTime.Now, Direct = true, Seconds = 6 });
            };
            Vpn.Start();
            Widgets.Start();
            Media.Start();
            Specs.LoadAsync();
            var tmr = new System.Windows.Forms.Timer(); tmr.Interval = 2000; tmr.Start();
            tmr.Tick += delegate
            {
                string e = NotificationWatcher.LastError;
                lblNotifStatus.Text = e != "" ? e : NotificationWatcher.Available ? "Lendo a central de notificações do Windows" : "Iniciando...";
                lblNotifStatus.ForeColor = e != "" ? Color.Firebrick : Color.SeaGreen;
                if (e != "") Log.Write("notificacoes", e);
                RgbTick();
                SaveSettings(); // salva os ajustes logo depois de feitos
                string air = OnAirText();
                grpOnAir.Text = air != "" ? "No ar: " + air : "No ar (aviso de microfone e câmera)";
            };
            worker = new Thread(Loop); worker.IsBackground = true; worker.Priority = ThreadPriority.BelowNormal; worker.Start();
            if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "/min")
            { WindowState = FormWindowState.Minimized; ShowInTaskbar = true; Load += delegate { Hide(); }; }
        }

        void LoadSettings()
        {
            mode = cfg.GetInt("mode", 0);
            bgColor = cfg.GetColor("bg", Color.FromArgb(12, 14, 22));
            accent = cfg.GetColor("accent", Color.FromArgb(0, 200, 255));
            fgColor = cfg.GetColor("fg", Color.White);
            rgbSync = cfg.GetBool("rgbsync", false);
            rgbPrevBg = cfg.GetColor("rgbprevbg", bgColor); rgbPrevAccent = cfg.GetColor("rgbprevaccent", accent); rgbPrevFg = cfg.GetColor("rgbprevfg", fgColor);
            title = cfg.Get("title", Environment.MachineName);
            bgImagePath = cfg.Get("bgimage", ""); imagePath = cfg.Get("image", "");
            textContent = cfg.Get("text", "Ola!").Replace("\\n", "\n");
            textSize = cfg.GetInt("textsize", 36);
            imageFill = cfg.GetBool("fill", true);
            LoadCards(); specMode = cfg.GetInt("specmode", 2); batWarn = cfg.GetBool("batwarn", true);
            clock24 = cfg.GetBool("clock24", true);
            mirrorFollowMouse = cfg.GetBool("follow", false);
            brightness = cfg.GetInt("brightness", 50); orientation = cfg.GetInt("orientation", 2);
            mirrorScreen = cfg.GetInt("screen", 0); mirrorFps = cfg.GetInt("fps", 2);
            notifEnabled = cfg.GetBool("notif", true); notifHideBody = cfg.GetBool("notifhide", false);
            notifSeconds = cfg.GetInt("notifsec", 8); notifIgnore = cfg.Get("notifignore", "");
            dashTheme = cfg.GetInt("theme", 0);
            musicToast = cfg.GetBool("musictoast", true);
            apiEnabled = cfg.GetBool("api", true); ApiServer.Port = cfg.GetInt("apiport", 48765); StatusBoard.Show = cfg.GetBool("statusstrip", true);
            OnAir.WatchMic = cfg.GetBool("onairmic", true); OnAir.WatchCam = cfg.GetBool("onaircam", true); OnAir.Ignore = cfg.Get("onairignore", "");
            LayoutStore.Load();
            PhotoFrame.Enabled = cfg.GetBool("photos", false); PhotoFrame.Folder = cfg.Get("photofolder", ""); PhotoFrame.Seconds = cfg.GetInt("photosec", 30);
            PhotoFrame.Recursive = cfg.GetBool("photosub", true); PhotoFrame.Shuffle = cfg.GetBool("photorand", true); PhotoFrame.ShowClock = cfg.GetBool("photoclock", true);
            Agenda.Sources = cfg.Get("calendar", ""); meetLead = cfg.GetInt("callead", 15);
            meetStrip = cfg.GetBool("calstrip", true); meetToast = cfg.GetBool("caltoast", true); meetHide = cfg.GetBool("calhide", false);
            SF.Family = cfg.Get("screenfont", ""); lcd.Transition = cfg.GetInt("transition", 0);
            vpnStrip = cfg.GetBool("vpnstrip", true); vpnToast = cfg.GetBool("vpntoast", true); vpnHide = cfg.GetBool("vpnhide", false);
            autoGame = cfg.GetBool("autogame", true);
            autoOn = cfg.GetBool("auto", false); autoMusic = cfg.GetBool("automusic", true);
            autoIdle = cfg.GetBool("autoidle", true); autoIdleMin = cfg.GetInt("autoidlemin", 10); autoIdleAction = cfg.GetInt("autoidleact", 0);
            autoRotate = cfg.GetBool("autorot", false); autoRotateSec = cfg.GetInt("autorotsec", 30);
            var rl = new List<int>(); foreach (var s in cfg.Get("autorotlist", "0,1,4").Split(',')) { int v; if (int.TryParse(s, out v)) rl.Add(v); }
            // versao 2 ganhou Temperaturas e Baterias no indice 8 e 9: os temas proprios andam 2 casas
            // versao 3 ganhou o tema VPN no indice 10
            int themeVer = cfg.GetInt("themever", 1);
            if (themeVer < 2)
            {
                if (dashTheme >= 8) dashTheme += 2;
                for (int i = 0; i < rl.Count; i++) if (rl[i] >= 8) rl[i] += 2;
            }
            if (themeVer < 3)
            {
                if (dashTheme >= 10) dashTheme += 1;
                for (int i = 0; i < rl.Count; i++) if (rl[i] >= 10) rl[i] += 1;
            }
            rotateThemes = rl.ToArray();
            autoNight = cfg.GetBool("night", false); nightPct = cfg.GetInt("nightpct", 15);
            nightFrom = cfg.GetInt("nightfrom", 22); nightTo = cfg.GetInt("nightto", 7);
            Widgets.City = cfg.Get("city", ""); Widgets.QuoteList = cfg.Get("quotes", "USD-BRL,EUR-BRL,BTC-BRL");
            LoadBg(); LoadImage();
        }

        void SaveSettings()
        {
            cfg.Set("mode", mode); cfg.Set("bg", bgColor); cfg.Set("accent", accent); cfg.Set("fg", fgColor);
            cfg.Set("rgbsync", rgbSync); cfg.Set("rgbprevbg", rgbPrevBg); cfg.Set("rgbprevaccent", rgbPrevAccent); cfg.Set("rgbprevfg", rgbPrevFg);
            cfg.Set("title", title); cfg.Set("bgimage", bgImagePath); cfg.Set("image", imagePath);
            cfg.Set("text", textContent.Replace("\r", "").Replace("\n", "\\n")); cfg.Set("textsize", textSize);
            cfg.Set("fill", imageFill); cfg.Set("cards", SaveCards()); cfg.Set("specmode", specMode); cfg.Set("batwarn", batWarn);
            cfg.Set("clock24", clock24); cfg.Set("follow", mirrorFollowMouse); cfg.Set("brightness", brightness);
            cfg.Set("orientation", orientation); cfg.Set("screen", mirrorScreen); cfg.Set("fps", mirrorFps);
            cfg.Set("notif", notifEnabled); cfg.Set("notifhide", notifHideBody); cfg.Set("notifsec", notifSeconds); cfg.Set("notifignore", notifIgnore); cfg.Set("theme", dashTheme);
            cfg.Set("musictoast", musicToast);
            cfg.Set("api", apiEnabled); cfg.Set("apiport", ApiServer.Port); cfg.Set("statusstrip", StatusBoard.Show);
            cfg.Set("onairmic", OnAir.WatchMic); cfg.Set("onaircam", OnAir.WatchCam); cfg.Set("onairignore", OnAir.Ignore);
            cfg.Set("photos", PhotoFrame.Enabled); cfg.Set("photofolder", PhotoFrame.Folder); cfg.Set("photosec", PhotoFrame.Seconds);
            cfg.Set("photosub", PhotoFrame.Recursive); cfg.Set("photorand", PhotoFrame.Shuffle); cfg.Set("photoclock", PhotoFrame.ShowClock);
            cfg.Set("calendar", Agenda.Sources); cfg.Set("callead", meetLead);
            cfg.Set("calstrip", meetStrip); cfg.Set("caltoast", meetToast); cfg.Set("calhide", meetHide);
            cfg.Set("screenfont", SF.IsDefault ? "" : SF.Family); cfg.Set("transition", lcd.Transition);
            cfg.Set("vpnstrip", vpnStrip); cfg.Set("vpntoast", vpnToast); cfg.Set("vpnhide", vpnHide);
            cfg.Set("autogame", autoGame);
            cfg.Set("auto", autoOn); cfg.Set("automusic", autoMusic); cfg.Set("autoidle", autoIdle); cfg.Set("autoidlemin", autoIdleMin);
            cfg.Set("autoidleact", autoIdleAction); cfg.Set("autorot", autoRotate); cfg.Set("autorotsec", autoRotateSec);
            var rs = new List<string>(); foreach (int t in rotateThemes) rs.Add(t.ToString()); cfg.Set("autorotlist", string.Join(",", rs.ToArray())); cfg.Set("themever", 3);
            cfg.Set("night", autoNight); cfg.Set("nightpct", nightPct); cfg.Set("nightfrom", nightFrom); cfg.Set("nightto", nightTo);
            cfg.Set("city", Widgets.City); cfg.Set("quotes", Widgets.QuoteList);
            cfg.Save();
        }

        void LoadBg()
        {
            lock (imgLock)
            {
                if (bgImage != null) { bgImage.Dispose(); bgImage = null; }
                try { if (bgImagePath != "" && File.Exists(bgImagePath)) bgImage = new Bitmap(bgImagePath); } catch { }
            }
        }

        void LoadImage()
        {
            lock (imgLock)
            {
                if (image != null) { image.Dispose(); image = null; }
                gifFrames = 0; gifFrame = 0;
                try
                {
                    if (imagePath == "" || !File.Exists(imagePath)) return;
                    image = Image.FromStream(new MemoryStream(File.ReadAllBytes(imagePath)));
                    gifDim = new FrameDimension(image.FrameDimensionsList[0]);
                    gifFrames = image.GetFrameCount(gifDim);
                    gifDelays = new int[gifFrames];
                    try
                    {
                        var pi = image.GetPropertyItem(0x5100);
                        for (int i = 0; i < gifFrames; i++) gifDelays[i] = Math.Max(20, BitConverter.ToInt32(pi.Value, i * 4) * 10);
                    }
                    catch { for (int i = 0; i < gifFrames; i++) gifDelays[i] = 100; }
                }
                catch { image = null; }
            }
        }

        // ---------------- UI
        Control L(string t, int x, int y, Control parent) { var l = new Label(); l.Text = t; l.AutoSize = true; l.Location = new Point(x, y + 3); parent.Controls.Add(l); return l; }
        T Add<T>(T c, int x, int y, int w, Control parent) where T : Control { c.Location = new Point(x, y); if (w > 0) c.Width = w; parent.Controls.Add(c); return c; }
        Button Btn(string t, int x, int y, int w, Control parent, EventHandler h) { var b = Add(new Button(), x, y, w, parent); b.Text = t; b.Height = 26; b.Click += h; return b; }
        CheckBox Chk(string t, bool v, int x, int y, Control parent, EventHandler h) { var c = Add(new CheckBox(), x, y, 0, parent); c.Text = t; c.AutoSize = true; c.Checked = v; c.CheckedChanged += h; return c; }

        void BuildUi()
        {
            // previa em cima, barra de status, abas embaixo (uma linha so; a janela alarga junto)
            preview = Add(new PictureBox(), 12, 12, PreviewW, this);
            preview.Height = PreviewH; preview.BorderStyle = BorderStyle.None; preview.SizeMode = PictureBoxSizeMode.Zoom; preview.BackColor = Color.Black;
            statusDot = (Label)L("●", 10, PreviewH + 15, this); statusDot.ForeColor = Color.Orange; statusDot.Font = new Font("Segoe UI", 12f);
            status = (Label)L("Procurando a tela...", 28, PreviewH + 18, this);
            lblNow = (Label)L("", 0, PreviewH + 18, this); lblNow.AutoSize = false; lblNow.TextAlign = ContentAlignment.TopRight; lblNow.ForeColor = Color.Gray; lblNow.Width = 300;
            var tip = new ToolTip();
            tip.SetToolTip(status, "Velocidade da tela: ~0,5 quadro/s em tela cheia. O app envia só o que muda.");
            tip.SetToolTip(preview, "Prévia do que está na tela USB");

            tabs = Add(new TabControl(), 12, PreviewH + 46, PreviewW, this); tabs.Height = TabsH;
            tabs.Multiline = false; tabs.Padding = new Point(10, 4);
            var tp = new TabPage("Painel"); var ti = new TabPage("Imagem / GIF"); var tm = new TabPage("Espelhar tela"); var tt = new TabPage("Texto");
            var tc = new TabPage("Cartões");
            var ta = new TabPage("Auto");
            var tg = new TabPage("Agenda");
            var tv = new TabPage("VPN");
            var tw = new TabPage("Geral");
            ti.Text = "Imagem"; tm.Text = "Espelho";
            tabs.TabPages.AddRange(new[] { tp, ti, tm, tt, tc, ta, tg, tv, tw });
            BuildCardsTab(tc);
            BuildAgendaTab(tg);
            BuildVpnTab(tv);
            BuildGeneralTab(tw);
            tabs.SelectedIndex = mode;
            // a aba Automatico nao e um modo: so as 4 primeiras escolhem o que mostrar
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedIndex < 4) { mode = tabs.SelectedIndex; lcd.Invalidate(); } };
            BuildAutoTab(ta);
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i + 1 < args.Length; i++)
                if (args[i] == "/editor") Load += delegate { OpenEditor(); };
                else if (args[i] == "/themeshots") { string shotDir = args[i + 1]; Load += delegate { ThemeShots(shotDir); }; }
                else if (args[i] == "/framedump") frameDumpDir = args[i + 1];
                else if (args[i] == "/framecount") int.TryParse(args[i + 1], out frameDumpLeft);
                else if (args[i] == "/batlog") Batteries.Debug = true;
                else if (args[i] == "/gamepid") { int gp; if (int.TryParse(args[i + 1], out gp)) Gaming.ForcePid = gp; }
                else if (args[i] == "/testonair") { int secs; if (int.TryParse(args[i + 1], out secs)) OnAir.TestUntil = DateTime.Now.AddSeconds(secs); }
                else if (args[i] == "/tab") { int ti2; if (int.TryParse(args[i + 1], out ti2) && ti2 >= 0 && ti2 < tabs.TabCount) tabs.SelectedIndex = ti2; }

            // painel
            L("Título", 10, 12, tp);
            txtTitle = Add(new TextBox(), 70, 12, 260, tp); txtTitle.Text = title; txtTitle.TextChanged += delegate { title = txtTitle.Text; };
            btnBg = Btn("Fundo", 10, 44, 100, tp, delegate { PickColor(ref bgColor, btnBg); });
            btnAccent = Btn("Destaque", 118, 44, 100, tp, delegate { PickColor(ref accent, btnAccent); });
            btnFg = Btn("Texto", 226, 44, 104, tp, delegate { PickColor(ref fgColor, btnFg); });
            Paint3();
            Btn("Imagem de fundo...", 10, 80, 130, tp, delegate { var p = Open("Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif"); if (p != null) { bgImagePath = p; LoadBg(); lblBg.Text = Path.GetFileName(p); } });
            Btn("Sem imagem", 146, 80, 90, tp, delegate { bgImagePath = ""; LoadBg(); lblBg.Text = "(nenhuma)"; });
            lblBg = (Label)L(bgImagePath == "" ? "(nenhuma)" : Path.GetFileName(bgImagePath), 242, 82, tp);
            lblBg.AutoSize = false; lblBg.Width = 96; lblBg.AutoEllipsis = true;
            L("Paleta", 10, 110, tp);
            cboPalette = Add(new ComboBox(), 70, 108, 266, tp);
            cboPalette.DropDownStyle = ComboBoxStyle.DropDownList; cboPalette.DrawMode = DrawMode.OwnerDrawFixed;
            cboPalette.ItemHeight = 20; cboPalette.MaxDropDownItems = 14;
            cboPalette.Items.Add("Personalizada");
            foreach (var p in Palettes) cboPalette.Items.Add(p.Name);
            cboPalette.DrawItem += DrawPaletteItem;
            cboPalette.SelectedIndex = MatchPalette() + 1;
            cboPalette.SelectedIndexChanged += delegate
            {
                if (syncingPalette || cboPalette.SelectedIndex <= 0) return;
                var p = Palettes[cboPalette.SelectedIndex - 1];
                LeaveRgbSync();
                bgColor = p.Bg; accent = p.Accent; fgColor = p.Fg; Paint3();
            };
            lblPanelHint = (Label)L(PanelHint, 10, 140, tp); lblPanelHint.ForeColor = Color.Gray;
            lblPanelHint.AutoSize = false; lblPanelHint.Width = 326; lblPanelHint.AutoEllipsis = true;
            chk24 = Chk("Relógio 24h", clock24, 10, 166, tp, delegate { clock24 = chk24.Checked; });
            chkRgb = Chk("Cores do RGB (SignalRGB)", rgbSync, 130, 166, tp, delegate { if (!syncingRgb) SetRgbSync(chkRgb.Checked); });
            L("Tema", 10, 200, tp);
            cboTheme = Add(new ComboBox(), 70, 198, 200, tp); cboTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTheme.Items.AddRange(AllThemeNames());
            dashTheme = Math.Max(0, Math.Min(cboTheme.Items.Count - 1, dashTheme));
            cboTheme.SelectedIndex = dashTheme;
            cboTheme.SelectedIndexChanged += delegate { if (!syncTheme) dashTheme = cboTheme.SelectedIndex; };
            Btn("Editar", 276, 197, 60, tp, delegate { OpenEditor(); });
            L("Cidade", 10, 232, tp);
            var txtCity = Add(new TextBox(), 70, 230, 200, tp); txtCity.Text = Widgets.City;
            txtCity.Leave += delegate { Widgets.City = txtCity.Text; };
            txtCity.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Widgets.City = txtCity.Text; e.SuppressKeyPress = true; } };
            L("Cotações", 10, 262, tp);
            var txtQuotes = Add(new TextBox(), 70, 260, 200, tp); txtQuotes.Text = Widgets.QuoteList;
            txtQuotes.Leave += delegate { Widgets.QuoteList = txtQuotes.Text; };
            Btn("Atualizar", 276, 244, 60, tp, delegate { Widgets.City = txtCity.Text; Widgets.QuoteList = txtQuotes.Text; Widgets.RefreshNow(); });

            // imagem
            Btn("Escolher imagem ou GIF...", 10, 12, 180, ti, delegate { var p = Open("Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif"); if (p != null) { imagePath = p; LoadImage(); lblImage.Text = Path.GetFileName(p); lcd.Invalidate(); } });
            lblImage = (Label)L(imagePath == "" ? "(nenhuma)" : Path.GetFileName(imagePath), 10, 46, ti);
            chkFill = Chk("Preencher a tela (corta as bordas)", imageFill, 10, 70, ti, delegate { imageFill = chkFill.Checked; });
            var n1 = L("GIFs animados funcionam, mas a tela é lenta:\nGIFs pequenos ou com pouca mudança ficam melhores.", 10, 98, ti); n1.ForeColor = Color.Gray;

            // porta-retrato
            CheckBox cPh = null, cSub = null, cRand = null, cClock = null;
            cPh = Chk("Porta-retrato: usar uma pasta de fotos", PhotoFrame.Enabled, 10, 140, ti, delegate { PhotoFrame.Enabled = cPh.Checked; lcd.Invalidate(); });
            cPh.Font = new Font(Font, FontStyle.Bold);
            Label lblFolder = null;
            Btn("Escolher pasta...", 10, 166, 120, ti, delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.SelectedPath = PhotoFrame.Folder;
                    if (d.ShowDialog(this) == DialogResult.OK) { PhotoFrame.Folder = d.SelectedPath; lblFolder.Text = Path.GetFileName(d.SelectedPath.TrimEnd('\\')); }
                }
            });
            lblFolder = (Label)L(PhotoFrame.Folder == "" ? "(nenhuma)" : Path.GetFileName(PhotoFrame.Folder.TrimEnd('\\')), 138, 168, ti);
            lblFolder.AutoSize = false; lblFolder.Width = 198; lblFolder.AutoEllipsis = true;
            L("Trocar a cada", 10, 200, ti);
            var nSec = Add(new NumericUpDown(), 92, 198, 56, ti); nSec.Minimum = 5; nSec.Maximum = 3600; nSec.Value = Math.Max(5, Math.Min(3600, PhotoFrame.Seconds));
            nSec.ValueChanged += delegate { PhotoFrame.Seconds = (int)nSec.Value; };
            L("s", 151, 200, ti);
            cSub = Chk("Incluir subpastas", PhotoFrame.Recursive, 190, 200, ti, delegate { PhotoFrame.Recursive = cSub.Checked; });
            cRand = Chk("Ordem aleatória", PhotoFrame.Shuffle, 10, 228, ti, delegate { PhotoFrame.Shuffle = cRand.Checked; });
            cClock = Chk("Relógio sobre a foto", PhotoFrame.ShowClock, 190, 228, ti, delegate { PhotoFrame.ShowClock = cClock.Checked; });
            Btn("Próxima foto", 10, 254, 100, ti, delegate { PhotoFrame.Next(); });
            var lblPh = (Label)L("", 118, 258, ti); lblPh.AutoSize = false; lblPh.Width = 218; lblPh.AutoEllipsis = true; lblPh.ForeColor = Color.SeaGreen;
            var tmPh = new System.Windows.Forms.Timer(); tmPh.Interval = 1500; tmPh.Start();
            tmPh.Tick += delegate { lblPh.Text = PhotoFrame.Enabled ? PhotoFrame.Status : ""; };

            // espelho
            L("Monitor", 10, 12, tm);
            cboScreen = Add(new ComboBox(), 70, 12, 260, tm); cboScreen.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (var s in Screen.AllScreens) cboScreen.Items.Add(s.DeviceName.Replace(@"\\.\", "") + "  " + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " (principal)" : ""));
            cboScreen.SelectedIndex = Math.Min(mirrorScreen, cboScreen.Items.Count - 1);
            cboScreen.SelectedIndexChanged += delegate { mirrorScreen = cboScreen.SelectedIndex; };
            chkFollow = Chk("Seguir o mouse (lupa 1:1 ao redor do cursor)", mirrorFollowMouse, 10, 44, tm, delegate { mirrorFollowMouse = chkFollow.Checked; });
            L("Quadros/s máx.", 10, 74, tm);
            numFps = Add(new NumericUpDown(), 110, 72, 60, tm); numFps.Minimum = 1; numFps.Maximum = 10; numFps.Value = Math.Max(1, Math.Min(10, mirrorFps));
            numFps.ValueChanged += delegate { mirrorFps = (int)numFps.Value; };
            var n2 = L("Não é um monitor de verdade (o Windows não vê a tela como\nvídeo). O app captura a imagem e envia. Funciona bem\npara conteúdo que muda pouco.", 10, 106, tm); n2.ForeColor = Color.Gray;

            // texto
            txtText = Add(new TextBox(), 10, 12, 326, tt); txtText.Multiline = true; txtText.Height = 160; txtText.ScrollBars = ScrollBars.Vertical;
            txtText.Text = textContent.Replace("\n", "\r\n"); txtText.TextChanged += delegate { textContent = txtText.Text.Replace("\r", ""); };
            L("Tamanho", 10, 184, tt);
            numTextSize = Add(new NumericUpDown(), 70, 182, 60, tt); numTextSize.Minimum = 10; numTextSize.Maximum = 120; numTextSize.Value = Math.Max(10, Math.Min(120, textSize));
            numTextSize.ValueChanged += delegate { textSize = (int)numTextSize.Value; };

            LayoutWindow();
        }

        const int PreviewW = 488, PreviewH = 326, TabsH = 330;
        Label statusDot, lblNow;

        // a largura acompanha as abas; o conteudo de cada aba fica centralizado e rola na vertical
        void LayoutWindow()
        {
            int strip = 8;
            foreach (TabPage p in tabs.TabPages) strip += TextRenderer.MeasureText(p.Text, tabs.Font).Width + tabs.Padding.X * 2 + 4;
            int w = Math.Max(PreviewW, strip);
            ClientSize = new Size(w + 24, PreviewH + 46 + TabsH + 12);
            preview.Left = 12 + (w - PreviewW) / 2;
            tabs.Width = w;
            lblNow.Left = 12 + w - lblNow.Width;
            int inner = w - 8;
            foreach (TabPage p in tabs.TabPages)
            {
                p.AutoScroll = true;
                int right = 0;
                foreach (Control c in p.Controls) right = Math.Max(right, c.Right);
                int dx = Math.Max(0, (inner - SystemInformation.VerticalScrollBarWidth - right - 10) / 2);
                if (dx > 0) foreach (Control c in p.Controls) c.Left += dx;
            }
        }

        // configuracoes gerais: tela, inicio, fonte, API, faixa de status e No ar
        void BuildGeneralTab(TabPage t)
        {
            var g = Add(new GroupBox(), 6, 4, 334, t); g.Text = "Tela"; g.Height = 94;
            L("Brilho", 10, 20, g);
            trkBright = Add(new TrackBar(), 70, 16, 254, g); trkBright.Minimum = 0; trkBright.Maximum = 100; trkBright.TickFrequency = 10; trkBright.Value = brightness;
            trkBright.ValueChanged += delegate { brightness = trkBright.Value; appliedBrightness = -1; };
            L("Orientação", 10, 60, g);
            cboOrient = Add(new ComboBox(), 80, 58, 122, g); cboOrient.DropDownStyle = ComboBoxStyle.DropDownList;
            cboOrient.Items.AddRange(new object[] { "Retrato", "Retrato invertido", "Paisagem", "Paisagem invertida" });
            cboOrient.SelectedIndex = orientation;
            cboOrient.SelectedIndexChanged += delegate { orientation = cboOrient.SelectedIndex; needConfig = true; };
            Btn("Desligar", 206, 57, 64, g, delegate { Safe(delegate { lcd.ScreenOn(false); }); });
            Btn("Ligar", 274, 57, 50, g, delegate { Safe(delegate { lcd.ScreenOn(true); lcd.Invalidate(); }); });

            chkStartup = Chk("Iniciar com o Windows (minimizado)", IsStartup(), 10, 106, t, delegate { SetStartup(chkStartup.Checked); });
            Btn("Reconectar a tela", 220, 102, 120, t, delegate { lock (lcdLock) lcd.Close(); });

            L("Fonte da tela", 10, 136, t);
            var cbFont = Add(new ComboBox(), 100, 134, 240, t); cbFont.DropDownStyle = ComboBoxStyle.DropDownList; cbFont.MaxDropDownItems = 16;
            cbFont.Items.Add(SF.Default + " (padrão)");
            foreach (var n in SF.List()) if (!n.Equals(SF.Default, StringComparison.OrdinalIgnoreCase)) cbFont.Items.Add(n);
            int fi = SF.IsDefault ? 0 : cbFont.Items.IndexOf(SF.Family);
            cbFont.SelectedIndex = Math.Max(0, fi);
            var sample = (Label)L("12:34  CPU 45%  Escritório", 100, 158, t);
            sample.AutoSize = false; sample.Width = 240; sample.Height = 22; sample.ForeColor = Color.DimGray;
            Action showSample = delegate
            {
                try { var old = sample.Font; sample.Font = new Font(SF.Family, 12f); if (old != Font) old.Dispose(); } catch { }
            };
            showSample();
            cbFont.SelectedIndexChanged += delegate
            {
                SF.Family = cbFont.SelectedIndex <= 0 ? "" : (string)cbFont.SelectedItem;
                showSample(); lcd.Invalidate(); // a tela toda e redesenhada na fonte nova
            };

            L("Transição", 10, 186, t);
            var cbTr = Add(new ComboBox(), 100, 184, 240, t); cbTr.DropDownStyle = ComboBoxStyle.DropDownList;
            cbTr.Items.AddRange(Lcd.TransitionNames);
            cbTr.SelectedIndex = Math.Max(0, Math.Min(Lcd.TransitionNames.Length - 1, lcd.Transition));
            cbTr.SelectedIndexChanged += delegate { lcd.Transition = cbTr.SelectedIndex; };

            CheckBox cApi = null, cStrip = null;
            cApi = Chk("API local (porta " + ApiServer.Port + ")", apiEnabled, 10, 218, t, delegate
            {
                apiEnabled = cApi.Checked;
                if (apiEnabled) ApiServer.Start(); else ApiServer.Stop();
            });
            Btn("Copiar token", 220, 214, 120, t, delegate { if (ApiServer.Token != "") Clipboard.SetText(ApiServer.Token); });
            cStrip = Chk("Faixa de status (Claude Code, reunião, VPN)", StatusBoard.Show, 10, 242, t, delegate { StatusBoard.Show = cStrip.Checked; });

            grpOnAir = Add(new GroupBox(), 6, 266, 334, t); grpOnAir.Text = "No ar (aviso de microfone e câmera)"; grpOnAir.Height = 70;
            CheckBox cMic = null, cCam = null;
            cMic = Chk("Microfone", OnAir.WatchMic, 10, 18, grpOnAir, delegate { OnAir.WatchMic = cMic.Checked; });
            cCam = Chk("Câmera", OnAir.WatchCam, 100, 18, grpOnAir, delegate { OnAir.WatchCam = cCam.Checked; });
            Btn("Testar", 244, 14, 80, grpOnAir, delegate { OnAir.TestUntil = DateTime.Now.AddSeconds(10); });
            L("Ignorar", 10, 42, grpOnAir);
            var txtAir = Add(new TextBox(), 66, 41, 258, grpOnAir); txtAir.Text = OnAir.Ignore;
            txtAir.TextChanged += delegate { OnAir.Ignore = txtAir.Text; };

            var gn = Add(new GroupBox(), 6, 340, 334, t); gn.Text = "Notificações do Windows (Teams, WhatsApp, e-mail...)"; gn.Height = 166;
            chkNotif = Chk("Mostrar notificações na tela", notifEnabled, 10, 18, gn, delegate { notifEnabled = chkNotif.Checked; });
            chkNotifHide = Chk("Privacidade: esconder o texto da mensagem", notifHideBody, 10, 40, gn, delegate { notifHideBody = chkNotifHide.Checked; });
            L("Mostrar por", 10, 66, gn);
            numNotifSec = Add(new NumericUpDown(), 85, 64, 50, gn); numNotifSec.Minimum = 3; numNotifSec.Maximum = 60; numNotifSec.Value = Math.Max(3, Math.Min(60, notifSeconds));
            numNotifSec.ValueChanged += delegate { notifSeconds = (int)numNotifSec.Value; };
            L("segundos", 140, 66, gn);
            Btn("Testar", 244, 62, 80, gn, delegate
            {
                var n = new ToastInfo(); n.App = "Microsoft Teams"; n.Title = "Maria Silva";
                n.Body = "Oi! Voce consegue dar uma olhada no relatorio antes da reuniao das 15h?"; n.Time = DateTime.Now; n.Icon = Icon.ToBitmap();
                NotificationWatcher.Inject(n);
            });
            L("Ignorar apps", 10, 94, gn);
            txtNotifIgnore = Add(new TextBox(), 90, 92, 234, gn); txtNotifIgnore.Text = notifIgnore;
            txtNotifIgnore.TextChanged += delegate { notifIgnore = txtNotifIgnore.Text; };
            chkMusicToast = Chk("Avisar quando trocar de música (fora do Tocando agora)", musicToast, 10, 120, gn, delegate { musicToast = chkMusicToast.Checked; });
            lblNotifStatus = (Label)L("Iniciando...", 10, 140, gn);
        }

        void Paint3()
        {
            btnBg.BackColor = bgColor; btnBg.ForeColor = Contrast(bgColor);
            btnAccent.BackColor = accent; btnAccent.ForeColor = Contrast(accent);
            btnFg.BackColor = fgColor; btnFg.ForeColor = Contrast(fgColor);
        }
        static Color Contrast(Color c) { return c.GetBrightness() > 0.5 ? Color.Black : Color.White; }

        void PickColor(ref Color c, Button b)
        {
            var d = new ColorDialog(); d.Color = c; d.FullOpen = true;
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                LeaveRgbSync();
                c = d.Color; Paint3();
                RefreshPaletteCombo();
            }
        }

        void RefreshPaletteCombo()
        {
            syncingPalette = true; cboPalette.SelectedIndex = MatchPalette() + 1; syncingPalette = false;
            cboPalette.Invalidate();
        }

        // ---------------- cores do RGB do PC (SignalRGB)
        const string PanelHint = "Cartões, ordem e especificações: aba Cartões";
        bool rgbSync, syncingRgb; int rgbLast = -1;
        Color rgbPrevBg, rgbPrevAccent, rgbPrevFg;
        CheckBox chkRgb; Label lblPanelHint;

        void SetRgbSync(bool on)
        {
            rgbSync = on; rgbLast = -1;
            if (on) { rgbPrevBg = bgColor; rgbPrevAccent = accent; rgbPrevFg = fgColor; RgbTick(); return; }
            bgColor = rgbPrevBg; accent = rgbPrevAccent; fgColor = rgbPrevFg;
            Paint3(); RefreshPaletteCombo(); ShowPanelHint(PanelHint, Color.Gray);
        }

        // escolher outra cor ou paleta encerra a sincronizacao e mantem a escolha
        void LeaveRgbSync()
        {
            if (!rgbSync) return;
            rgbSync = false; rgbLast = -1;
            syncingRgb = true; chkRgb.Checked = false; syncingRgb = false;
            ShowPanelHint(PanelHint, Color.Gray);
        }

        void ShowPanelHint(string text, Color color) { lblPanelHint.Text = text; lblPanelHint.ForeColor = color; }

        void RgbTick()
        {
            if (!rgbSync) return;
            var rgb = RgbSync.Read(RgbSync.DefaultKey);
            if (!rgb.Found) { ShowPanelHint("SignalRGB nao encontrado: cores mantidas", Color.Firebrick); return; }
            if (!rgb.HasColor) { ShowPanelHint("RGB: " + rgb.Effect + " nao tem cor fixa, cores mantidas", Color.DarkGoldenrod); return; }
            ShowPanelHint("RGB: " + rgb.Effect + " " + ColorTranslator.ToHtml(Color.FromArgb(rgb.Color.ToArgb())), Color.SeaGreen);
            if (rgb.Color.ToArgb() == rgbLast) return;
            rgbLast = rgb.Color.ToArgb();
            RgbSync.Harmonize(rgb.Color, out bgColor, out accent, out fgColor);
            Paint3(); RefreshPaletteCombo();
        }

        // ---------------- paletas pre-definidas (fundo, destaque, texto)
        class Palette
        {
            public string Name; public Color Bg, Accent, Fg;
            public Palette(string n, string bg, string acc, string fg)
            { Name = n; Bg = ColorTranslator.FromHtml(bg); Accent = ColorTranslator.FromHtml(acc); Fg = ColorTranslator.FromHtml(fg); }
        }

        static readonly Palette[] Palettes =
        {
            new Palette("Oceano",              "#0C0E16", "#00C8FF", "#FFFFFF"),
            new Palette("Tokyo Night",         "#1A1B26", "#7AA2F7", "#C0CAF5"),
            new Palette("Catppuccin Mocha",    "#1E1E2E", "#CBA6F7", "#CDD6F4"),
            new Palette("Dracula",             "#282A36", "#FF79C6", "#F8F8F2"),
            new Palette("Nord",                "#2E3440", "#88C0D0", "#ECEFF4"),
            new Palette("Solarized",           "#002B36", "#2AA198", "#EEE8D5"),
            new Palette("Gruvbox",             "#282828", "#FABD2F", "#EBDBB2"),
            new Palette("Floresta",            "#0F1A14", "#4ADE80", "#E7F5EC"),
            new Palette("Por do sol",          "#1A1012", "#FF8A5B", "#FFE8DC"),
            new Palette("Cereja",              "#1B0E14", "#F43F5E", "#FFE4E6"),
            new Palette("Ouro e grafite",      "#161616", "#D4AF37", "#F5F1E6"),
            new Palette("Lavanda",             "#16131F", "#B794F4", "#EDE7F6"),
            new Palette("Cyberpunk",           "#0D0221", "#FF2A6D", "#05D9E8"),
            new Palette("Matrix",              "#000000", "#00FF41", "#B6FFC8"),
            new Palette("Monocromatico",       "#000000", "#FFFFFF", "#BDBDBD"),
            new Palette("Claro - papel",       "#F4F1EA", "#2563EB", "#1F2937"),
            new Palette("Claro - menta",       "#ECFDF5", "#059669", "#064E3B"),
        };

        ComboBox cboPalette; bool syncingPalette;

        int MatchPalette()
        {
            for (int i = 0; i < Palettes.Length; i++)
                if (Palettes[i].Bg.ToArgb() == bgColor.ToArgb() && Palettes[i].Accent.ToArgb() == accent.ToArgb() && Palettes[i].Fg.ToArgb() == fgColor.ToArgb()) return i;
            return -1;
        }

        void DrawPaletteItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            var r = e.Bounds;
            Color bg = bgColor, ac = accent, fg = fgColor;
            if (e.Index > 0) { var p = Palettes[e.Index - 1]; bg = p.Bg; ac = p.Accent; fg = p.Fg; }
            // amostra: retangulo do fundo com uma barra de destaque e um "Aa" na cor do texto
            var sw = new Rectangle(r.X + 3, r.Y + 2, 58, r.Height - 4);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, sw);
            using (var b = new SolidBrush(ac)) e.Graphics.FillRectangle(b, sw.X + 4, sw.Bottom - 6, 26, 3);
            using (var f = new Font("Segoe UI Semibold", 8f)) using (var b = new SolidBrush(fg)) e.Graphics.DrawString("Aa", f, b, sw.X + 3, sw.Y);
            using (var b = new SolidBrush(ac)) e.Graphics.FillEllipse(b, sw.Right - 14, sw.Y + 3, 9, 9);
            using (var pen = new Pen(Color.FromArgb(90, 128, 128, 128))) e.Graphics.DrawRectangle(pen, sw);
            using (var b = new SolidBrush(e.ForeColor)) e.Graphics.DrawString((string)cboPalette.Items[e.Index], e.Font, b, r.X + 68, r.Y + 2);
            e.DrawFocusRectangle();
        }

        string Open(string filter)
        {
            var d = new OpenFileDialog(); d.Filter = filter;
            return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        bool IsStartup() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("TurzxScreen") != null; }
        void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("TurzxScreen", "\"" + Application.ExecutablePath + "\" /min");
                else k.DeleteValue("TurzxScreen", false);
            }
        }

        void Safe(Action a) { lock (lcdLock) { try { if (lcd.IsOpen) a(); } catch (Exception ex) { SetStatus("Erro: " + ex.Message); lcd.Close(); } } }
        void SetStatus(string s)
        {
            try
            {
                BeginInvoke((Action)delegate
                {
                    status.Text = s;
                    statusDot.ForeColor = s.StartsWith("Conectado") ? Color.FromArgb(76, 199, 120)
                        : s.StartsWith("Erro") || s.Contains("não encontrada") || s.Contains("em uso") ? Color.Firebrick : Color.Orange;
                });
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false; SaveSettings();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            try { PresentTrace.Stop(); } catch { }
            LayoutStore.Save();
            try { worker.Join(3000); } catch { }
            // app fechando (ou Windows desligando): apaga a tela para nao ficar com informacao congelada
            ScreenOffNow();
            lcd.Close(); tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ---------------- energia: suspender / voltar / desligar
        readonly object lcdLock = new object();
        volatile bool suspended, reconnectNow;
        bool apiEnabled = true;

        void ScreenOffNow()
        {
            lock (lcdLock)
            {
                try { if (lcd.IsOpen) { lcd.ScreenOn(false); Thread.Sleep(150); } } catch { }
                screenIsOff = true;
            }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                suspended = true;
                ScreenOffNow();
                lock (lcdLock) lcd.Close(); // a porta costuma ficar invalida depois de dormir
                SetStatus("PC suspenso: tela apagada");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                lock (lcdLock) lcd.Close();
                needConfig = true; reconnectNow = true;
                suspended = false;   // o loop reconecta, religa a tela e redesenha tudo
                SetStatus("Voltando da suspensão: reconectando...");
            }
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            // desligar / reiniciar / sair da conta
            suspended = true;
            ScreenOffNow();
        }

        // ---------------- loop de render
        // a previa so existe com a janela aberta, e nunca acumula quadros na fila da interface
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        IntPtr formHandle; int previewBusy; bool previewShown;

        void PostPreview(Bitmap frame)
        {
            bool open = formHandle != IntPtr.Zero && IsWindowVisible(formHandle) && !IsIconic(formHandle);
            if (!open && !previewShown) return;
            if (Interlocked.CompareExchange(ref previewBusy, 1, 0) != 0) return;
            previewShown = open;
            var copy = open ? new Bitmap(frame) : null; // o quadro e reaproveitado pela thread de render
            BeginInvoke((Action)delegate
            {
                var old = preview.Image; preview.Image = copy; if (old != null) old.Dispose();
                previewBusy = 0;
            });
        }

        string frameDumpDir; int frameDumpLeft, frameDumpIndex;

        // desenvolvimento: guarda quadros reais para comparar o envio
        void DumpFrame(Bitmap frame)
        {
            if (frameDumpDir == null || frameDumpLeft <= 0) return;
            frame.Save(Path.Combine(frameDumpDir, "f" + (frameDumpIndex++).ToString("000") + ".png"), ImageFormat.Png);
            if (--frameDumpLeft == 0) BeginInvoke((Action)delegate { Close(); });
        }

        bool lowPriority;

        void Loop()
        {
            DateTime nextTry = DateTime.MinValue;
            bool askedKill = false;
            Bitmap frame = null;
            while (running)
            {
                // PC suspenso / desligando: nao desenha nem reabre a porta
                if (suspended) { Thread.Sleep(200); continue; }
                if (reconnectNow) { reconnectNow = false; nextTry = DateTime.MinValue; }
                var t0 = DateTime.Now;
                if (!lcd.IsOpen && DateTime.Now >= nextTry)
                {
                    nextTry = DateTime.Now.AddSeconds(3);
                    string port = Lcd.FindPort();
                    if (port == null) SetStatus("Tela não encontrada. Verifique o cabo USB.");
                    else
                    {
                        try { lock (lcdLock) { if (suspended) continue; lcd.Open(port); } needConfig = true; SetStatus("Conectado em " + port); }
                        catch (UnauthorizedAccessException)
                        {
                            SetStatus(port + " em uso por outro programa (app TURZX?)");
                            if (!askedKill) { askedKill = true; AskKillTurzx(); }
                        }
                        catch (Exception ex) { SetStatus("Erro ao abrir " + port + ": " + ex.Message); }
                    }
                }
                int wait = 500;
                // ---- troca automatica
                int effMode = mode, effTheme = dashTheme;
                bool idleOff = false;
                ApplyAuto(ref effMode, ref effTheme, ref idleOff);
                if (lcd.IsOpen && !needConfig)
                {
                    lock (lcdLock)
                    try
                    {
                        if (suspended) continue;
                        if (idleOff != screenIsOff) { lcd.ScreenOn(!idleOff); screenIsOff = idleOff; if (!idleOff) lcd.Invalidate(); }
                        int b = EffectiveBrightness();
                        if (b != appliedBrightness) { lcd.SetBrightness(b); appliedBrightness = b; }
                    }
                    catch { }
                }
                if (effMode != lastEffMode || effTheme != lastEffTheme) { lastEffMode = effMode; lastEffTheme = effTheme; SetAutoStatus(effMode, effTheme); }
                // jogo aberto: o app cede a vez ao jogo em qualquer disputa de CPU
                bool gaming = Gaming.Active;
                if (gaming != lowPriority)
                {
                    lowPriority = gaming;
                    try { using (var me = Process.GetCurrentProcess()) me.PriorityClass = gaming ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; } catch { }
                }
                if (idleOff) { SysInfo.Update(true); Thread.Sleep(500); continue; }
                try
                {
                    SysInfo.Update();
                    int w = lcd.Width, h = lcd.Height;
                    if (needConfig) { w = orientation >= 2 ? 480 : 320; h = orientation >= 2 ? 320 : 480; }
                    if (frame == null || frame.Width != w || frame.Height != h)
                    {
                        if (frame != null) frame.Dispose();
                        frame = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    }
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.Clear(Color.Black);
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        TickMeetings(); TickVpn();
                        int stripCount;
                        stripInset = StatusBoard.Show && StatusBoard.Top(out stripCount) != null ? 22 : 0;
                        switch (effMode)
                        {
                            case 0: DrawDashboard(g, w, h, effTheme); wait = 1000 - DateTime.Now.Millisecond + 5; break;
                            case 1: wait = DrawImage(g, w, h); break;
                            case 2: DrawMirror(g, w, h); wait = 1000 / Math.Max(1, mirrorFps); break;
                            default: DrawText(g, w, h); wait = 500; break;
                        }
                        // faixa nova -> cartao (se nao estiver ja no tema de musica)
                        int tv = Media.TrackVersion;
                        if (tv != seenTrack)
                        {
                            bool first = seenTrack < 0; seenTrack = tv;
                            if (!first && musicToast && Media.Playing && !(effMode == 0 && effTheme == 5))
                            {
                                var t = new ToastInfo(); t.App = (Media.Source != "" ? Media.Source + "  ·  " : "") + "Tocando agora";
                                t.Title = Media.Title; t.Body = Media.Artist; t.Time = DateTime.Now; t.IsMusic = true;
                                var c = Media.Cover; if (c != null) try { t.Icon = new Bitmap(c); } catch { }
                                NotificationWatcher.Inject(t);
                            }
                        }
                        // imagem enviada pela API e faixa de status
                        var apiImage = ApiServer.Overlay;
                        if (apiImage != null && DateTime.Now < ApiServer.OverlayUntil) { g.Clear(Color.Black); DrawCover(g, apiImage, w, h, false); }
                        if (StatusBoard.Show) DrawStatusStrip(g, w, h);
                        // notificacao por cima de qualquer modo
                        if (curToast != null && DateTime.Now >= toastUntil) curToast = null;
                        if (curToast == null)
                        {
                            ToastInfo t;
                            while ((t = NotificationWatcher.Dequeue()) != null)
                            {
                                if (!t.IsMusic && !t.Direct && (!notifEnabled || IsIgnored(t.App))) continue;
                                curToastSeconds = t.Seconds > 0 ? t.Seconds : notifSeconds;
                                curToast = t; toastUntil = DateTime.Now.AddSeconds(curToastSeconds); break;
                            }
                        }
                        if (curToast != null)
                        {
                            DrawToast(g, w, h, curToast);
                            wait = Math.Min(wait, 500);
                        }
                        if (OnAir.Active) DrawOnAir(g, w, h);
                    }
                    if (lcd.IsOpen && !suspended)
                    {
                        lock (lcdLock)
                        try
                        {
                            if (suspended || !lcd.IsOpen) throw new OperationCanceledException();
                            if (needConfig)
                            {
                                needConfig = false;
                                // sempre religa: a tela pode ter ficado "desligada" por suspensao/desligamento anterior
                                lcd.ScreenOn(true); screenIsOff = false;
                                lcd.SetOrientation(orientation);
                                appliedBrightness = EffectiveBrightness(); lcd.SetBrightness(appliedBrightness);
                            }
                            lcd.Push(frame);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Log.Error("tela", ex);
                            SetStatus("Conexão perdida (" + ex.Message + "). Reconectando...");
                            lcd.Close(); nextTry = DateTime.Now.AddSeconds(2);
                        }
                    }
                    DumpFrame(frame);
                    PostPreview(frame);
                }
                catch (Exception ex)
                {
                    // erro ao desenhar: nao derruba a conexao, so tenta de novo no proximo quadro
                    Log.Error("desenho", ex);
                    SetStatus("Erro ao desenhar: " + ex.Message);
                }
                int spent = (int)(DateTime.Now - t0).TotalMilliseconds;
                Thread.Sleep(Math.Max(10, wait - spent));
            }
            if (frame != null) frame.Dispose();
        }

        // ---------------- troca automatica
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
        static double IdleMinutes()
        {
            var li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref li)) return 0;
            return unchecked((uint)Environment.TickCount - li.dwTime) / 60000.0;
        }

        // temas prontos; os criados no editor vem depois deles (indice 8 em diante)
        static readonly string[] ThemeNames = { "Cartões", "Gráficos (histórico)", "Relógio grande", "Medidores", "Clima e cotações", "Tocando agora (música)", "Gaming (FPS)", "Agenda", "Temperaturas", "Baterias", "VPN" };
        const int VpnTheme = 10;
        CheckedListBox rotateList; bool syncTheme;

        static string[] AllThemeNames()
        {
            var l = new List<string>(ThemeNames); l.AddRange(LayoutStore.Names());
            return l.ToArray();
        }

        // recarrega as listas de temas sem mexer no tema escolhido
        void RefreshThemeLists()
        {
            var names = AllThemeNames();
            syncTheme = true;
            cboTheme.BeginUpdate(); cboTheme.Items.Clear(); cboTheme.Items.AddRange(names);
            dashTheme = Math.Max(0, Math.Min(names.Length - 1, dashTheme));
            cboTheme.SelectedIndex = dashTheme; cboTheme.EndUpdate();
            syncTheme = false;
            FillRotateList();
        }

        class RotItem { public int Index; public string Name; public override string ToString() { return Name; } }
        bool fillingRot;

        // marcados primeiro, na ordem do rodizio; depois os outros na ordem normal
        void FillRotateList()
        {
            if (rotateList == null) return;
            var names = AllThemeNames(); var rt = rotateThemes;
            fillingRot = true;
            rotateList.BeginUpdate(); rotateList.Items.Clear();
            var used = new HashSet<int>();
            foreach (int t in rt)
                if (t >= 0 && t < names.Length && used.Add(t)) rotateList.Items.Add(new RotItem { Index = t, Name = names[t] }, true);
            for (int t = 0; t < names.Length; t++)
                if (!used.Contains(t)) rotateList.Items.Add(new RotItem { Index = t, Name = names[t] }, false);
            rotateList.EndUpdate();
            fillingRot = false;
        }

        void ReadRotateList()
        {
            var sel = new List<int>();
            for (int i = 0; i < rotateList.Items.Count; i++)
                if (rotateList.GetItemChecked(i)) sel.Add(((RotItem)rotateList.Items[i]).Index);
            rotateThemes = sel.ToArray();
        }

        void MoveRotate(int dir)
        {
            int i = rotateList.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= rotateList.Items.Count) return;
            object a = rotateList.Items[i]; bool ca = rotateList.GetItemChecked(i), cb = rotateList.GetItemChecked(j);
            fillingRot = true;
            rotateList.Items[i] = rotateList.Items[j]; rotateList.SetItemChecked(i, cb);
            rotateList.Items[j] = a; rotateList.SetItemChecked(j, ca);
            rotateList.SelectedIndex = j;
            fillingRot = false;
            ReadRotateList();
        }
        static readonly string[] ModeNames = { "Painel", "Imagem / GIF", "Espelhar tela", "Texto" };

        bool autoOn, autoMusic, autoIdle, autoRotate, autoNight, screenIsOff, autoGame = true;
        int autoIdleMin = 10, autoIdleAction, autoRotateSec = 30, nightPct = 15, nightFrom = 22, nightTo = 7;
        volatile int[] rotateThemes = new int[0];
        int appliedBrightness = -1, lastEffMode = -1, lastEffTheme = -1, rotateIdx; DateTime rotateNext = DateTime.MinValue;
        Label lblAuto;

        void ApplyAuto(ref int effMode, ref int effTheme, ref bool off)
        {
            if (editorOpen) { effMode = 0; effTheme = editTheme; return; } // editando: mostra o layout ao vivo na tela
            if (!autoOn) return;
            if (autoGame && Gaming.Active) { effMode = 0; effTheme = 6; return; } // jogando com controle nao conta como PC parado
            // em chamada a pessoa pode so estar ouvindo: nao conta como PC parado
            if (autoIdle && !OnAir.Active && IdleMinutes() >= autoIdleMin)
            {
                if (autoIdleAction == 0) { effMode = 0; effTheme = 2; }
                else if (autoIdleAction == 1) { effMode = 1; }
                else off = true;
                return;
            }
            if (autoMusic && Media.Has && Media.Playing) { effMode = 0; effTheme = 5; return; }
            var rt = rotateThemes;
            if (autoRotate && rt.Length > 0)
            {
                if (DateTime.Now >= rotateNext) { rotateIdx = (rotateIdx + 1) % rt.Length; rotateNext = DateTime.Now.AddSeconds(Math.Max(5, autoRotateSec)); }
                effMode = 0; effTheme = rt[rotateIdx % rt.Length];
            }
        }

        int EffectiveBrightness()
        {
            if (autoOn && autoNight)
            {
                int hr = DateTime.Now.Hour;
                bool night = nightFrom > nightTo ? (hr >= nightFrom || hr < nightTo) : (hr >= nightFrom && hr < nightTo);
                if (night) return Math.Min(brightness, nightPct);
            }
            return brightness;
        }

        void SetAutoStatus(int m, int t)
        {
            var names = AllThemeNames();
            string s = "Mostrando agora: " + (m == 0 ? "Painel - " + names[Math.Max(0, Math.Min(names.Length - 1, t))] : ModeNames[Math.Min(3, m)]);
            try { BeginInvoke((Action)delegate { if (lblAuto != null) lblAuto.Text = s; if (lblNow != null) lblNow.Text = s; }); } catch { }
        }

        void BuildAutoTab(TabPage ta)
        {
            CheckBox cOn = null, cMus = null, cIdle = null, cRot = null, cNight = null;
            cOn = Chk("Ativar troca automática", autoOn, 10, 8, ta, delegate { autoOn = cOn.Checked; appliedBrightness = -1; });
            cOn.Font = new Font(Font, FontStyle.Bold);
            CheckBox cGame = null;
            cGame = Chk("Jogo aberto -> Gaming", autoGame, 190, 8, ta, delegate { autoGame = cGame.Checked; });
            cMus = Chk("Música tocando -> tema Tocando agora", autoMusic, 10, 34, ta, delegate { autoMusic = cMus.Checked; });

            cIdle = Chk("PC parado por", autoIdle, 10, 62, ta, delegate { autoIdle = cIdle.Checked; });
            var nIdle = Add(new NumericUpDown(), 112, 60, 48, ta); nIdle.Minimum = 1; nIdle.Maximum = 240; nIdle.Value = Math.Max(1, Math.Min(240, autoIdleMin));
            nIdle.ValueChanged += delegate { autoIdleMin = (int)nIdle.Value; };
            L("min ->", 164, 62, ta);
            var cbIdle = Add(new ComboBox(), 208, 60, 128, ta); cbIdle.DropDownStyle = ComboBoxStyle.DropDownList;
            cbIdle.Items.AddRange(new object[] { "Relógio grande", "Imagem / fotos", "Desligar a tela" });
            cbIdle.SelectedIndex = Math.Max(0, Math.Min(2, autoIdleAction));
            cbIdle.SelectedIndexChanged += delegate { autoIdleAction = cbIdle.SelectedIndex; };

            cRot = Chk("Alternar temas a cada", autoRot(), 10, 92, ta, delegate { autoRotate = cRot.Checked; });
            var nRot = Add(new NumericUpDown(), 160, 90, 52, ta); nRot.Minimum = 5; nRot.Maximum = 3600; nRot.Value = Math.Max(5, Math.Min(3600, autoRotateSec));
            nRot.ValueChanged += delegate { autoRotateSec = (int)nRot.Value; };
            L("segundos, nesta ordem:", 216, 92, ta);
            var list = Add(new CheckedListBox(), 28, 114, 270, ta); list.Height = 76; list.CheckOnClick = true; list.IntegralHeight = false;
            rotateList = list;
            FillRotateList();
            list.ItemCheck += (s, e) => { if (!fillingRot) BeginInvoke((Action)ReadRotateList); };
            Btn("▲", 304, 114, 32, ta, delegate { MoveRotate(-1); });
            Btn("▼", 304, 152, 32, ta, delegate { MoveRotate(1); });

            cNight = Chk("Brilho noturno", autoNight, 10, 196, ta, delegate { autoNight = cNight.Checked; appliedBrightness = -1; });
            var nPct = Add(new NumericUpDown(), 120, 194, 46, ta); nPct.Minimum = 0; nPct.Maximum = 100; nPct.Value = Math.Max(0, Math.Min(100, nightPct));
            nPct.ValueChanged += delegate { nightPct = (int)nPct.Value; appliedBrightness = -1; };
            L("% das", 168, 196, ta);
            var nFrom = Add(new NumericUpDown(), 210, 194, 40, ta); nFrom.Minimum = 0; nFrom.Maximum = 23; nFrom.Value = nightFrom;
            nFrom.ValueChanged += delegate { nightFrom = (int)nFrom.Value; appliedBrightness = -1; };
            L("as", 254, 196, ta);
            var nTo = Add(new NumericUpDown(), 274, 194, 40, ta); nTo.Minimum = 0; nTo.Maximum = 23; nTo.Value = nightTo;
            nTo.ValueChanged += delegate { nightTo = (int)nTo.Value; appliedBrightness = -1; };
            L("h", 318, 196, ta);

            lblAuto = (Label)L("", 10, 232, ta); lblAuto.ForeColor = Color.SeaGreen;
            var hint = L("Prioridade: jogo > PC parado > música > rodízio > modo.\nAs notificações aparecem por cima de tudo.", 10, 256, ta);
            hint.ForeColor = Color.Gray;
        }
        bool autoRot() { return autoRotate; }

        void AskKillTurzx()
        {
            Invoke((Action)delegate
            {
                var ps = Process.GetProcessesByName("UsbMonitor");
                if (ps.Length == 0) return;
                if (MessageBox.Show(this, "O app TURZX (UsbMonitor) esta usando a tela.\nFechar ele agora para este app assumir?", "TURZX Screen",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var p in ps) { try { p.Kill(); } catch (Exception ex) { MessageBox.Show(this, "Nao consegui fechar: " + ex.Message + "\nFeche o TURZX manualmente."); } }
            });
        }

        void DrawBackground(Graphics g, int w, int h)
        {
            g.Clear(bgColor);
            lock (imgLock) { if (bgImage != null) DrawCover(g, bgImage, w, h, true); }
        }

        static void DrawCover(Graphics g, Image img, int w, int h, bool fill)
        {
            double sx = (double)w / img.Width, sy = (double)h / img.Height;
            double s = fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
            int dw = (int)(img.Width * s), dh = (int)(img.Height * s);
            g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
        }

        void DrawDashboard(Graphics g, int w, int h, int theme)
        {
            if (theme >= LayoutStore.First) { DrawCustom(g, w, h, theme); return; }
            switch (theme)
            {
                case 1: DrawGraphs(g, w, h); return;
                case 2: DrawBigClock(g, w, h); return;
                case 3: DrawGauges(g, w, h); return;
                case 4: DrawWeather(g, w, h); return;
                case 5: DrawMusic(g, w, h); return;
                case 6: DrawGaming(g, w, h); return;
                case 7: DrawAgenda(g, w, h); return;
                case 8: DrawTemps(g, w, h); return;
                case 9: DrawBatteries(g, w, h); return;
                case VpnTheme: DrawVpnTheme(g, w, h); return;
                default: DrawCards(g, w, h); return;
            }
        }

        static readonly CultureInfo PT = new CultureInfo("pt-BR");
        string TimeStr(bool seconds) { return DateTime.Now.ToString(clock24 ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "hh:mm:ss" : "hh:mm"), PT); }
        static string DateStr() { string s = DateTime.Now.ToString("dddd, d 'de' MMMM", PT); return char.ToUpper(s[0]) + s.Substring(1); }

        // cabecalho compacto: hora a esquerda, data e titulo a direita
        int DrawHeader(Graphics g, int w, Brush fg, Brush dim, Brush acc)
        {
            using (var fT = SF.New("Segoe UI Semibold", 34))
            using (var fS = SF.New("Segoe UI", 14))
            {
                g.DrawString(TimeStr(true), fT, fg, 10, 2);
                using (var sf = new StringFormat()) { sf.Alignment = StringAlignment.Far;
                    g.DrawString(DateStr(), fS, dim, new RectangleF(0, 10, w - 12, 20), sf);
                    g.DrawString(title, fS, acc, new RectangleF(0, 28, w - 12, 20), sf); }
            }
            return 52;
        }

        // Grafico de "varredura": so o cursor anda, entao pouca coisa muda por segundo (a tela e lenta)
        static void DrawSweep(Graphics g, Rectangle r, float[] hist, Brush bar) { DrawSweep(g, r, hist, bar, SysInfo.HCount); }

        static void DrawSweep(Graphics g, Rectangle r, float[] hist, Brush bar, int count)
        {
            const int step = 3;
            int n = r.Width / step, latest = count - 1;
            if (latest < 0 || n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                int s = latest - (((latest - i) % n) + n) % n;
                if (s < 0 || s < count - n + 4) continue; // 4 slots vazios na frente do cursor
                float v = Math.Max(0, Math.Min(100, hist[s % hist.Length]));
                int bh = Math.Max(1, (int)(r.Height * v / 100));
                g.FillRectangle(bar, r.X + i * step, r.Bottom - bh, step - 1, bh);
            }
        }

        void DrawGraphs(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
            using (var acc = new SolidBrush(accent)) using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var fL = SF.New("Segoe UI Semibold", 13))
            using (var fV = SF.New("Segoe UI Semibold", 26))
            using (var fX = SF.New("Segoe UI", 12))
            {
                int top = DrawHeader(g, w, fg, dim, acc);
                // segue a ordem da aba Cartoes (so CPU, RAM e GPU tem historico)
                var rows = new List<object[]>();
                foreach (var id in cardOrder)
                {
                    if (!cardOn.Contains(id)) continue;
                    if (id == "cpu") rows.Add(new object[] { "CPU", SysInfo.Cpu.ToString("0") + "%", CpuTemp.Value >= 0 ? CpuTemp.Value.ToString("0") + " °C" : "", SysInfo.HCpu });
                    else if (id == "ram") rows.Add(new object[] { "RAM", SysInfo.RamPct.ToString("0") + "%", SysInfo.RamUsedGb.ToString("0.0") + " GB", SysInfo.HRam });
                    else if (id == "gpu" && SysInfo.GpuPct >= 0) rows.Add(new object[] { "GPU", SysInfo.GpuPct.ToString("0") + "%", SysInfo.GpuTemp.ToString("0") + " °C", SysInfo.HGpu });
                }
                if (rows.Count == 0) rows.Add(new object[] { "CPU", SysInfo.Cpu.ToString("0") + "%", "", SysInfo.HCpu });
                int pad = 8, rh = (h - top - pad * (rows.Count + 1) - (showNet ? 22 : 0) - stripInset) / rows.Count;
                int labelW = w > h ? 96 : 84;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = new Rectangle(pad, top + pad + i * (rh + pad), w - pad * 2, rh);
                    using (var p = Round(r, 8)) g.FillPath(panel, p);
                    g.DrawString((string)rows[i][0], fL, acc, r.X + 8, r.Y + 6);
                    g.DrawString((string)rows[i][1], fV, fg, r.X + 6, r.Y + 22);
                    g.DrawString((string)rows[i][2], fX, dim, r.X + 8, r.Y + 56);
                    var gr = new Rectangle(r.X + labelW, r.Y + 6, r.Width - labelW - 8, r.Height - 12);
                    DrawSweep(g, gr, (float[])rows[i][3], acc);
                }
                if (showNet)
                    g.DrawString("↓ " + SysInfo.Rate(SysInfo.NetDown) + "    ↑ " + SysInfo.Rate(SysInfo.NetUp), fX, dim, pad + 4, h - 20 - stripInset);
            }
        }

        void DrawBigClock(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor)))
            using (var acc = new SolidBrush(accent))
            using (var fBig = SF.New("Segoe UI Light", w > h ? 132 : 104))
            using (var fSec = SF.New("Segoe UI Light", 40))
            using (var fDate = SF.New("Segoe UI", 20))
            using (var fS = SF.New("Segoe UI", 14))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                string hm = TimeStr(false), ss = DateTime.Now.ToString("ss");
                var szHm = g.MeasureString(hm, fBig, 1000, sf); var szS = g.MeasureString(ss, fSec, 1000, sf);
                float total = szHm.Width + 8 + szS.Width, x = (w - total) / 2, y = h / 2 - szHm.Height / 2 - 30;
                g.DrawString(hm, fBig, fg, x, y, sf);
                g.DrawString(ss, fSec, acc, x + szHm.Width + 8, y + szHm.Height - szS.Height - 14, sf);
                using (var c = new StringFormat()) { c.Alignment = StringAlignment.Center;
                    g.DrawString(DateStr(), fDate, dim, new RectangleF(0, y + szHm.Height + 4, w, 30), c);
                    string stats = "CPU " + SysInfo.Cpu.ToString("0") + "%   RAM " + SysInfo.RamPct.ToString("0") + "%";
                    if (showGpu && SysInfo.GpuPct >= 0) stats += "   GPU " + SysInfo.GpuPct.ToString("0") + "% " + SysInfo.GpuTemp.ToString("0") + "°C";
                    if (Widgets.HasWeather) stats = Widgets.Place + " " + Widgets.Temp.ToString("0") + "°   ·   " + stats;
                    g.DrawString(stats, fS, acc, new RectangleF(0, h - 28 - stripInset, w, 24), c); }
            }
        }

        void DrawGauges(Graphics g, int w, int h)
        {
            // os 3 primeiros cartoes com porcentagem/temperatura, na ordem da aba Cartoes
            var items = new List<object[]>();
            foreach (var id in cardOrder)
            {
                if (items.Count >= 3 || !cardOn.Contains(id)) continue;
                var cd = BuildCard(id, false);
                if (cd == null || cd.Pct < 0) continue;
                string lbl = id == "gpu" && !cardOn.Contains("gputemp") ? "GPU " + SysInfo.GpuTemp.ToString("0") + "°C" : cd.Label;
                items.Add(new object[] { lbl, cd.Pct, cd });
            }
            if (items.Count == 0) items.Add(new object[] { "CPU", SysInfo.Cpu, new CardData() });
            DrawGaugeSet(g, w, h, items, showNet ? "↓ " + SysInfo.Rate(SysInfo.NetDown) + "    ↑ " + SysInfo.Rate(SysInfo.NetUp) : null);
        }

        // ---------------- tema Temperaturas: CPU, GPU e SSD em medidores, ventoinhas no rodape
        static readonly string[] TempIds = { "cputemp", "gputemp", "ssdtemp" };

        List<object[]> TempItems()
        {
            var items = new List<object[]>();
            foreach (var id in TempIds)
            {
                var cd = BuildCard(id, false);
                if (cd == null || cd.Pct < 0) continue;
                items.Add(new object[] { cd.Label.Replace("TEMP. ", ""), cd.Pct, cd });
            }
            return items;
        }

        string TempFooter()
        {
            var parts = new List<string>();
            if (Lhm.CaseFan >= 0) parts.Add("Ventoinhas " + Lhm.CaseFan.ToString("0") + " RPM");
            if (Lhm.GpuFan >= 0) parts.Add("Fan GPU " + Lhm.GpuFan.ToString("0") + " RPM");
            return parts.Count > 0 ? string.Join("   ·   ", parts.ToArray()) : null;
        }

        void DrawTemps(Graphics g, int w, int h)
        {
            var items = TempItems();
            if (items.Count > 0) { DrawGaugeSet(g, w, h, items, TempFooter()); return; }
            DrawBackground(g, w, h);
            var cd = BuildCard("cputemp", false);
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor))) using (var acc = new SolidBrush(accent))
            using (var f = SF.New("Segoe UI", 16)) using (var c = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                int top = DrawHeader(g, w, fg, dim, acc);
                g.DrawString("Temperaturas indisponíveis\n" + (cd != null && cd.Sub != "" ? cd.Sub : ""), f, dim, new RectangleF(0, top, w, h - top - stripInset), c);
            }
        }

        // ---------------- tema Baterias: um painel por periferico
        void DrawBatteries(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
            using (var acc = new SolidBrush(accent)) using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var barBg = new SolidBrush(Color.FromArgb(50, fgColor)))
            using (var near = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var far = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                int top = DrawHeader(g, w, fg, dim, acc), pad = 8;
                var list = new List<BatteryDevice>(Batteries.List);
                int bottom = h - pad - stripInset;
                if (list.Count == 0)
                    using (var f = SF.New("Segoe UI", 16)) using (var c = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    { g.DrawString("Nenhum periférico sem fio conectado", f, dim, new RectangleF(0, top, w, bottom - top), c); return; }
                int max = Math.Max(1, (bottom - top - pad) / (34 + pad));
                if (list.Count > max) list.RemoveRange(max, list.Count - max);
                int n = list.Count, rh = Math.Min(w > h ? 96 : 104, (bottom - top - pad * n) / n);
                bool big = rh >= 60;
                using (var fL = SF.New("Segoe UI Semibold", big ? 13 : 12))
                using (var fN = SF.New("Segoe UI", big ? 13 : 11))
                using (var fV = SF.New("Segoe UI Semibold", big ? Math.Min(34, rh / 2) : Math.Max(14, rh / 2)))
                    for (int i = 0; i < n; i++)
                    {
                        var d = list[i];
                        var r = new Rectangle(pad, top + pad + i * (rh + pad), w - pad * 2, rh);
                        using (var p = Round(r, 8)) g.FillPath(panel, p);
                        Brush main = d.Asleep ? dim : fg;
                        string pct = d.Percent >= 0 ? d.Percent + "%" : d.Charging ? "carregando" : "—";
                        string kind = Batteries.KindName(d.Kind).ToUpperInvariant() + (d.Charging ? "  ⚡" : "");
                        string name = d.Name + (d.Asleep ? " · dormindo" : "") + (d.Via != "" ? "  ·  " + d.Via : "");
                        int barH = big ? 8 : 5, barY = r.Bottom - barH - (big ? 10 : 6);
                        int pctW = (int)g.MeasureString("100%", fV).Width + 6;
                        var textR = new Rectangle(r.X + 10, r.Y + 2, r.Width - 20 - pctW, barY - r.Y - 2);
                        if (big)
                        {
                            g.DrawString(kind, fL, acc, new RectangleF(textR.X, textR.Y + 4, textR.Width, textR.Height / 2), near);
                            g.DrawString(name, fN, dim, new RectangleF(textR.X, textR.Y + textR.Height / 2, textR.Width, textR.Height / 2), near);
                        }
                        else
                        {
                            float kw = g.MeasureString(kind, fL).Width;
                            g.DrawString(kind, fL, acc, new RectangleF(textR.X, textR.Y, textR.Width, textR.Height), near);
                            g.DrawString(name, fN, dim, new RectangleF(textR.X + kw + 4, textR.Y, Math.Max(1, textR.Width - kw - 4), textR.Height), near);
                        }
                        g.DrawString(pct, fV, main, new RectangleF(r.Right - 10 - pctW - 40, r.Y + 2, pctW + 40, barY - r.Y - 2), far);
                        var b = new Rectangle(r.X + 10, barY, r.Width - 20, barH);
                        using (var bp = Round(b, barH / 2)) g.FillPath(barBg, bp);
                        if (d.Percent > 0)
                        {
                            b.Width = Math.Max(barH, (int)(b.Width * Math.Min(100, d.Percent) / 100.0));
                            using (var bb = new SolidBrush(d.Asleep ? Color.FromArgb(120, BatColor(d.Percent)) : BatColor(d.Percent)))
                            using (var bp = Round(b, barH / 2)) g.FillPath(bb, bp);
                        }
                    }
            }
        }

        void DrawGaugeSet(Graphics g, int w, int h, List<object[]> items, string footer)
        {
            DrawBackground(g, w, h);
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
            using (var acc = new SolidBrush(accent))
            using (var fV = SF.New("Segoe UI Semibold", 26))
            using (var fL = SF.New("Segoe UI", 13))
            using (var c = new StringFormat())
            {
                c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                int top = DrawHeader(g, w, fg, dim, acc);
                bool showNet = footer != null;
                int cols = w > h ? items.Count : 1;
                int rowsN = (items.Count + cols - 1) / cols;
                int bottom = (showNet ? h - 26 : h - 6) - stripInset;
                int cellW = w / cols, cellH = (bottom - top) / rowsN;
                int d = Math.Min(cellW, cellH) - 26;
                using (var bgPen = new Pen(Color.FromArgb(60, fgColor), 12))
                using (var pen = new Pen(accent, 12))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    for (int i = 0; i < items.Count; i++)
                    {
                        int cx = (i % cols) * cellW + cellW / 2, cy = top + (i / cols) * cellH + cellH / 2;
                        var r = new Rectangle(cx - d / 2, cy - d / 2, d, d);
                        double v = Math.Max(0, Math.Min(100, (double)items[i][1]));
                        var icd = (CardData)items[i][2];
                        bool isTemp = icd.Temp;
                        pen.Color = CardColor(icd, v);
                        g.DrawArc(bgPen, r, 135, 270);
                        if (v > 0.5) g.DrawArc(pen, r, 135, (float)(270 * v / 100));
                        g.DrawString(v.ToString("0") + (isTemp ? "°C" : "%"), fV, fg, new RectangleF(r.X, r.Y - 6, r.Width, r.Height), c);
                        g.DrawString((string)items[i][0], fL, acc, new RectangleF(r.X - 20, r.Bottom - 22, r.Width + 40, 20), c);
                    }
                }
                if (showNet)
                    using (var c2 = new StringFormat()) { c2.Alignment = StringAlignment.Center;
                        g.DrawString(footer, fL, dim, new RectangleF(0, h - 22 - stripInset, w, 20), c2); }
            }
        }

        // ---------------- reunioes
        int meetLead = 15; bool meetStrip = true, meetToast = true, meetHide, meetShown;
        readonly HashSet<string> meetWarned = new HashSet<string>();

        string MeetingTitle(Meeting m) { return meetHide ? "Reunião" : m.Title; }

        static string MeetingWhen(Meeting m)
        {
            var now = DateTime.Now;
            if (m.Start <= now) return "agora";
            int min = (int)Math.Ceiling((m.Start - now).TotalMinutes);
            if (min < 60) return "em " + min + " min";
            if (min < 720) return "em " + (min / 60) + " h" + (min % 60 > 0 ? " " + (min % 60).ToString("00") : "");
            if (m.Start.Date == now.Date) return m.Start.ToString("HH:mm");
            if (m.Start.Date == now.Date.AddDays(1)) return "amanhã " + m.Start.ToString("HH:mm");
            return m.Start.ToString("ddd HH:mm", PT);
        }

        void TickMeetings()
        {
            var m = Agenda.Next(); var now = DateTime.Now;
            bool show = false;
            if (m != null)
            {
                double left = (m.Start - now).TotalMinutes;
                if (meetToast && left > 0)
                    foreach (int mark in new[] { 5, 1 })
                        if (left <= mark && left > mark - 1 && meetWarned.Add(m.Key + "|" + mark))
                        {
                            var t = new ToastInfo(); t.App = "Agenda"; t.Title = MeetingTitle(m);
                            t.Body = "Começa em " + mark + " min" + (m.Place != "" && !meetHide ? "  ·  " + m.Place : "");
                            t.Time = now; t.Direct = true; t.Seconds = 12;
                            NotificationWatcher.Inject(t);
                        }
                if (meetStrip && (left <= meetLead))
                {
                    var i = new StatusItem(); i.Key = "meeting"; i.Label = "Reunião"; i.Detail = MeetingTitle(m); i.Expires = now.AddSeconds(90);
                    if (left <= 0) { i.State = "info"; i.Text = "termina em " + (int)Math.Ceiling((m.End - now).TotalMinutes) + " min"; }
                    else { i.State = left <= 5 ? "waiting" : "info"; i.Text = MeetingWhen(m); }
                    StatusBoard.Set(i, false);
                    show = true;
                }
            }
            if (!show && meetShown) StatusBoard.Remove("meeting");
            meetShown = show;
        }

        void DrawAgenda(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            int top;
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor))) using (var acc = new SolidBrush(accent))
                top = DrawHeader(g, w, fg, dim, acc);
            DrawAgendaBox(g, new Rectangle(8, top + 4, w - 16, h - 8 - stripInset - top - 4));
        }

        void DrawAgendaBox(Graphics g, Rectangle box)
        {
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor)))
            using (var acc = new SolidBrush(accent)) using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var fBig = SF.New("Segoe UI Light", 36))
            using (var fTitle = SF.New("Segoe UI Semibold", 18))
            using (var fRow = SF.New("Segoe UI Semibold", 15))
            using (var fS = SF.New("Segoe UI", 13))
            using (var ell = new StringFormat()) using (var right = new StringFormat()) using (var mid = new StringFormat())
            {
                ell.Trimming = StringTrimming.EllipsisCharacter; ell.FormatFlags = StringFormatFlags.NoWrap;
                right.Alignment = StringAlignment.Far; mid.Alignment = StringAlignment.Center; mid.LineAlignment = StringAlignment.Center;
                int bottom = box.Bottom;
                var now = DateTime.Now; var up = new List<Meeting>();
                foreach (var it in Agenda.Items) if (it.End > now) up.Add(it);
                var hero = new Rectangle(box.X, box.Y, box.Width, Math.Min(112, box.Height));
                using (var p = Round(hero, 8)) g.FillPath(panel, p);
                if (up.Count == 0)
                {
                    string msg = Agenda.Error != "" ? "Não consegui ler a agenda" : Agenda.Sources.Trim() == "" ? "Configure o calendário na aba Agenda" : "Nenhuma reunião nos próximos dias";
                    g.DrawString(msg, fRow, dim, hero, mid);
                    return;
                }
                var m = up[0]; bool live = m.Start <= now;
                g.DrawString(live ? "EM ANDAMENTO" : "PRÓXIMA REUNIÃO", fS, acc, hero.X + 10, hero.Y + 6);
                g.DrawString(live ? "termina em " + (int)Math.Ceiling((m.End - now).TotalMinutes) + " min" : MeetingWhen(m), fBig, fg, new RectangleF(hero.X + 6, hero.Y + 20, hero.Width - 12, 46), ell);
                g.DrawString(MeetingTitle(m), fTitle, fg, new RectangleF(hero.X + 10, hero.Y + 65, hero.Width - 20, 24), ell);
                string info = m.Start.ToString("HH:mm") + " – " + m.End.ToString("HH:mm") + (m.Place != "" && !meetHide ? "  ·  " + m.Place : "");
                g.DrawString(info, fS, dim, new RectangleF(hero.X + 10, hero.Y + 92, hero.Width - 20, 18), ell);

                int y = hero.Bottom + 6, rowH = 30;
                for (int i = 1; i < up.Count && y + rowH <= bottom; i++, y += rowH + 4)
                {
                    var r = new Rectangle(box.X, y, box.Width, rowH);
                    using (var p = Round(r, 6)) g.FillPath(panel, p);
                    string when = up[i].Start.Date == now.Date ? up[i].Start.ToString("HH:mm") : up[i].Start.ToString("ddd HH:mm", PT);
                    g.DrawString(when, fS, acc, new RectangleF(r.X + 8, r.Y + 6, 82, 18), ell);
                    g.DrawString(MeetingTitle(up[i]), fRow, fg, new RectangleF(r.X + 92, r.Y + 5, r.Width - 100, 20), ell);
                }
            }
        }

        void BuildVpnTab(TabPage t)
        {
            CheckBox strip = null, toast = null, hide = null;
            strip = Chk("Mostrar na faixa de status enquanto conectada", vpnStrip, 10, 10, t, delegate { vpnStrip = strip.Checked; });
            toast = Chk("Aviso na tela ao conectar e desconectar", vpnToast, 10, 36, t, delegate { vpnToast = toast.Checked; });
            hide = Chk("Privacidade: esconder o nome do perfil", vpnHide, 10, 62, t, delegate { vpnHide = hide.Checked; });
            var status = (Label)L("", 10, 94, t); status.AutoSize = false; status.Width = 326; status.Height = 52;
            var help = L("Cartão \"VPN conectada\" na aba Cartões e nos widgets.\n\nReconhece: OpenVPN GUI (com o perfil), OpenVPN\nConnect, WatchGuard, FortiClient, Check Point,\nSonicWall, DrayTek, GlobalProtect e as VPNs do\nWindows (com o nome da conexão). Só lê as placas\nde rede, nada é alterado.", 10, 152, t);
            help.ForeColor = Color.Gray;
            var tm = new System.Windows.Forms.Timer(); tm.Interval = 1500; tm.Start();
            tm.Tick += delegate
            {
                var l = Vpn.List;
                status.ForeColor = l.Count > 0 ? Color.SeaGreen : Color.Gray;
                if (l.Count == 0) { status.Text = "Nenhuma VPN conectada."; return; }
                var s = "Conectada:"; foreach (var c in l) s += "\n" + Vpn.Describe(c, false);
                status.Text = s;
            };
        }

        void BuildAgendaTab(TabPage t)
        {
            L("Link ICS do calendário (ou arquivo .ics):", 8, 8, t);
            var txt = Add(new TextBox(), 10, 30, 326, t); txt.Text = Agenda.Sources; txt.UseSystemPasswordChar = true;
            txt.Leave += delegate { Agenda.Sources = txt.Text; };
            txt.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Agenda.Sources = txt.Text; e.SuppressKeyPress = true; } };
            CheckBox show = null, strip = null, toast = null, hide = null;
            show = Chk("Mostrar link", false, 10, 56, t, delegate { txt.UseSystemPasswordChar = !show.Checked; });
            Btn("Atualizar", 256, 54, 80, t, delegate { Agenda.Sources = txt.Text; Agenda.RefreshNow(); });
            strip = Chk("Faixa de contagem regressiva", meetStrip, 10, 88, t, delegate { meetStrip = strip.Checked; });
            L("desde", 200, 88, t);
            var lead = Add(new NumericUpDown(), 240, 86, 48, t); lead.Minimum = 1; lead.Maximum = 120; lead.Value = Math.Max(1, Math.Min(120, meetLead));
            lead.ValueChanged += delegate { meetLead = (int)lead.Value; };
            L("min", 292, 88, t);
            toast = Chk("Aviso na tela 5 min e 1 min antes", meetToast, 10, 114, t, delegate { meetToast = toast.Checked; });
            hide = Chk("Privacidade: esconder título e local", meetHide, 10, 140, t, delegate { meetHide = hide.Checked; });
            var status = (Label)L("", 10, 168, t); status.AutoSize = false; status.Width = 326; status.Height = 34;
            var help = L("Outlook na web: Configurações > Calendário >\nCalendários compartilhados > Publicar um calendário\n> Pode ver todos os detalhes > copie o link ICS.\nGoogle: Configurações do calendário > Endereço\nsecreto no formato iCal.", 10, 206, t);
            help.ForeColor = Color.Gray;
            var tm = new System.Windows.Forms.Timer(); tm.Interval = 1500; tm.Start();
            tm.Tick += delegate
            {
                var m = Agenda.Next();
                status.ForeColor = Agenda.Error != "" ? Color.Firebrick : Color.SeaGreen;
                status.Text = Agenda.Error != "" ? Agenda.Error
                    : Agenda.Sources.Trim() == "" ? "Nenhum calendário configurado."
                    : Agenda.LastSync == DateTime.MinValue ? "Lendo calendário..."
                    : Agenda.Items.Count + " compromissos em 14 dias." + (m != null ? " Próximo: " + MeetingWhen(m) + "." : "");
            };
        }

        void DrawGaming(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            bool land = w > h;
            double fps = Gaming.Fps;
            Color fpsColor = fps < 0 ? fgColor : fps < 30 ? Color.FromArgb(239, 83, 80) : fps < 55 ? Color.FromArgb(255, 167, 38) : accent;
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(165, fgColor)))
            using (var acc = new SolidBrush(accent)) using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var fpsB = new SolidBrush(fpsColor))
            using (var fTitle = SF.New("Segoe UI Semibold", 16))
            using (var fLabel = SF.New("Segoe UI Semibold", 11))
            using (var fVal = SF.New("Segoe UI Semibold", 20))
            using (var fS = SF.New("Segoe UI", 13))
            using (var ell = new StringFormat()) using (var right = new StringFormat()) using (var center = new StringFormat())
            {
                ell.Trimming = StringTrimming.EllipsisCharacter; ell.FormatFlags = StringFormatFlags.NoWrap;
                right.Alignment = StringAlignment.Far; center.Alignment = StringAlignment.Center;
                string name = Gaming.Name != "" ? Gaming.Name : "Nenhum jogo aberto";
                g.DrawString(name, fTitle, acc, new RectangleF(10, 7, w - 86, 22), ell);
                g.DrawString(TimeStr(false), fTitle, fg, new RectangleF(0, 7, w - 10, 22), right);

                int top = 34, pad = 8;
                var fr = land ? new Rectangle(pad, top, 190, 132) : new Rectangle(pad, top, w - pad * 2, 118);
                var tr = land ? new Rectangle(fr.Right + pad, top, w - fr.Right - pad * 2, 132) : new Rectangle(pad, fr.Bottom + pad, w - pad * 2, 150);
                using (var p = Round(fr, 8)) g.FillPath(panel, p);
                DrawFpsBox(g, fr);

                var tiles = new[] {
                    new object[] { "GPU", SysInfo.GpuPct >= 0 ? SysInfo.GpuPct.ToString("0") + "%" : "—", -1.0 },
                    new object[] { "TEMP. GPU", SysInfo.GpuTemp >= 0 ? SysInfo.GpuTemp.ToString("0") + " °C" : "—", SysInfo.GpuTemp },
                    new object[] { "CPU", SysInfo.Cpu.ToString("0") + "%", -1.0 },
                    new object[] { "TEMP. CPU", CpuTemp.Value >= 0 ? CpuTemp.Value.ToString("0") + " °C" : "—", CpuTemp.Value },
                    new object[] { "RAM", SysInfo.RamUsedGb.ToString("0.0", PT) + " GB", -1.0 },
                    new object[] { "CLOCK GPU", Lhm.GpuClock >= 0 ? Lhm.GpuClock.ToString("0") + " MHz" : "—", -1.0 } };
                int gap = 6, tw = (tr.Width - gap) / 2, th = (tr.Height - gap * 2) / 3;
                for (int i = 0; i < tiles.Length; i++)
                {
                    int x = tr.X + (i % 2) * (tw + gap), y = tr.Y + (i / 2) * (th + gap);
                    using (var p = Round(new Rectangle(x, y, tw, th), 6)) g.FillPath(panel, p);
                    g.DrawString((string)tiles[i][0], fLabel, acc, new RectangleF(x + 6, y + 3, tw - 8, 14), ell);
                    double temp = (double)tiles[i][2];
                    using (var vb = new SolidBrush(temp >= 0 ? TempColor(temp) : fgColor))
                        g.DrawString((string)tiles[i][1], fVal, vb, new RectangleF(x + 5, y + 15, tw - 8, 26), ell);
                }

                int gy = tr.Bottom + pad, gh = h - gy - pad - stripInset;
                if (gh >= 34)
                {
                    var gr = new Rectangle(pad, gy, w - pad * 2, gh);
                    using (var p = Round(gr, 8)) g.FillPath(panel, p);
                    g.DrawString("HISTÓRICO DE FPS", fLabel, dim, gr.X + 8, gr.Y + 4);
                    if (Gaming.Source != "") g.DrawString(Gaming.Source, fLabel, dim, new RectangleF(gr.X, gr.Y + 4, gr.Width - 8, 14), right);
                    DrawSweep(g, new Rectangle(gr.X + 8, gr.Y + 20, gr.Width - 16, gr.Height - 25), Gaming.Hist, fpsB, Gaming.HCount);
                }
            }
        }

        void DrawFpsBox(Graphics g, Rectangle r)
        {
            double fps = Gaming.Fps;
            Color fpsColor = fps < 0 ? fgColor : fps < 30 ? Color.FromArgb(239, 83, 80) : fps < 55 ? Color.FromArgb(255, 167, 38) : accent;
            int px = Math.Max(16, Math.Min(74, (int)Math.Round(r.Height * 0.56)));
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(165, fgColor))) using (var fpsB = new SolidBrush(fpsColor))
            using (var fFps = SF.New("Segoe UI Semibold", px))
            using (var fLabel = SF.New("Segoe UI Semibold", 11))
            using (var fS = SF.New("Segoe UI", 13))
            using (var center = new StringFormat())
            {
                center.Alignment = StringAlignment.Center;
                g.DrawString(fps >= 0 ? fps.ToString("0") : "—", fFps, fpsB, new RectangleF(r.X, r.Y - 3, r.Width, px * 88 / 74), center);
                g.DrawString("FPS", fLabel, dim, new RectangleF(r.X, r.Y + px + 6, r.Width, 15), center);
                string sub = fps < 0 ? "sem leitura de FPS"
                    : (Gaming.Low1 > 0 ? "1% low " + Gaming.Low1.ToString("0") + "   " : "") + Gaming.FrameMs.ToString("0.0", PT) + " ms";
                g.DrawString(sub, fS, fg, new RectangleF(r.X, r.Y + px + 26, r.Width, 20), center);
            }
        }

        Bitmap musicBackdrop; int musicBackdropVer = -1, musicBackdropW, musicBackdropH;

        static string Fmt(TimeSpan t) { return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }

        void DrawMusic(Graphics g, int w, int h)
        {
            if (!Media.Has) { DrawBigClock(g, w, h); return; }
            lock (widgetLock) DrawPlayer(g, w, h); // o editor pode estar desenhando a mesma capa
            using (var fClock = SF.New("Segoe UI Light", 30))
            using (var white = new SolidBrush(Color.White)) using (var right = new StringFormat())
            {
                right.Alignment = StringAlignment.Far;
                g.DrawString(TimeStr(false), fClock, white, new RectangleF(0, w > h ? h - 42 - stripInset : 2, w - 12, 40), right);
            }
        }

        // capa, faixa e progresso, sobre a capa desfocada
        void DrawPlayer(Graphics g, int w, int h)
        {
            var cover = Media.Cover;
            // fundo: capa desfocada e escurecida (so recalcula quando a faixa muda)
            if (musicBackdropVer != Media.TrackVersion || musicBackdropW != w || musicBackdropH != h)
            {
                if (musicBackdrop != null) musicBackdrop.Dispose();
                musicBackdrop = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var bg = Graphics.FromImage(musicBackdrop))
                {
                    bg.Clear(bgColor);
                    if (cover != null)
                        using (var tiny = new Bitmap(10, 10))
                        {
                            using (var tg = Graphics.FromImage(tiny)) { tg.InterpolationMode = InterpolationMode.HighQualityBilinear; tg.DrawImage(cover, 0, 0, 10, 10); }
                            bg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            bg.DrawImage(tiny, new Rectangle(-w / 8, -h / 8, w + w / 4, h + h / 4));
                        }
                    using (var dark = new SolidBrush(Color.FromArgb(165, 0, 0, 0))) bg.FillRectangle(dark, 0, 0, w, h);
                }
                musicBackdropVer = Media.TrackVersion; musicBackdropW = w; musicBackdropH = h;
            }
            g.DrawImageUnscaled(musicBackdrop, 0, 0);

            bool land = w > h;
            int cs = Math.Max(16, land ? Math.Min(200, h - 16) : Math.Min(236, Math.Min(w - 24, h / 2)));
            var cr = land ? new Rectangle(Math.Min(16, w / 20), Math.Max(4, (h - cs) / 2 - 8), cs, cs) : new Rectangle((w - cs) / 2, Math.Min(40, h / 12), cs, cs);
            using (var fSmall = SF.New("Segoe UI Semibold", 12))
            using (var fTitle = SF.New("Segoe UI Semibold", 23))
            using (var fArtist = SF.New("Segoe UI", 17))
            using (var fAlbum = SF.New("Segoe UI", 14))
            using (var white = new SolidBrush(Color.White)) using (var dim = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            using (var acc = new SolidBrush(accent))
            using (var ell = new StringFormat()) using (var ell1 = new StringFormat()) using (var right = new StringFormat())
            {
                ell.Trimming = StringTrimming.EllipsisWord;
                ell1.Trimming = StringTrimming.EllipsisCharacter; ell1.FormatFlags = StringFormatFlags.NoWrap;
                right.Alignment = StringAlignment.Far;
                if (!land) { ell.Alignment = StringAlignment.Center; ell1.Alignment = StringAlignment.Center; }

                // capa com sombra
                using (var sh = Round(new Rectangle(cr.X + 3, cr.Y + 5, cr.Width, cr.Height), 12))
                using (var sb = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillPath(sb, sh);
                using (var path = Round(cr, 12))
                {
                    if (cover != null) { var clip = g.Clip; g.SetClip(path); g.DrawImage(cover, cr); g.Clip = clip; }
                    else using (var nb = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) { g.FillPath(nb, path);
                        using (var fn = SF.New("Segoe UI Symbol", 80)) using (var c = new StringFormat()) {
                            c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center; g.DrawString("♪", fn, dim, cr, c); } }
                }

                int tx = land ? cr.Right + 16 : 12, tw = land ? w - tx - 14 : w - 24;
                int ty = land ? cr.Y : cr.Bottom + 14;
                string state = (Media.Playing ? "TOCANDO AGORA" : "PAUSADO") + (Media.Source != "" ? "  ·  " + Media.Source.ToUpperInvariant() : "");
                g.DrawString(state, fSmall, acc, new RectangleF(tx, ty, tw, 16), ell1);
                var titleSz = g.MeasureString(Media.Title, fTitle, tw, ell);
                int titleH = (int)Math.Min(titleSz.Height, 62);
                g.DrawString(Media.Title, fTitle, white, new RectangleF(tx, ty + 20, tw, titleH), ell);
                g.DrawString(Media.Artist, fArtist, dim, new RectangleF(tx, ty + 24 + titleH, tw, 22), ell1);
                if (Media.Album != "" && Media.Album != Media.Title)
                    g.DrawString(Media.Album, fAlbum, dim, new RectangleF(tx, ty + 48 + titleH, tw, 20), ell1);

                // progresso
                if (Media.Duration > TimeSpan.FromSeconds(1))
                {
                    var pos = Media.CurrentPosition;
                    int by = land ? cr.Bottom - 26 : h - 44;
                    var bar = new Rectangle(tx, by, tw, 5);
                    using (var bb = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(bb, bar);
                    int pw = (int)(bar.Width * Math.Min(1, pos.TotalSeconds / Media.Duration.TotalSeconds));
                    if (pw > 0) g.FillRectangle(acc, bar.X, bar.Y, pw, bar.Height);
                    g.DrawString(Fmt(pos), fAlbum, dim, tx, by + 8);
                    g.DrawString(Fmt(Media.Duration), fAlbum, dim, new RectangleF(tx, by + 8, tw, 20), right);
                }
            }
        }

        void DrawWeather(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            int top;
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor))) using (var acc = new SolidBrush(accent))
                top = DrawHeader(g, w, fg, dim, acc);
            bool land = w > h;
            var wr = land ? new Rectangle(8, top + 6, w / 2 - 12, h - top - 14) : new Rectangle(8, top + 6, w - 16, (h - top) / 2 - 10);
            var qr = land ? new Rectangle(w / 2 + 4, top + 6, w / 2 - 12, h - top - 14) : new Rectangle(8, top + (h - top) / 2 + 2, w - 16, (h - top) / 2 - 10);
            using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            {
                using (var p = Round(wr, 8)) g.FillPath(panel, p);
                using (var p = Round(qr, 8)) g.FillPath(panel, p);
            }
            DrawWeatherBox(g, wr);
            DrawQuotesBox(g, qr);
        }

        void DrawWeatherBox(Graphics g, Rectangle wr)
        {
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor)))
            using (var acc = new SolidBrush(accent))
            using (var fTemp = SF.New("Segoe UI Light", 50))
            using (var fM = SF.New("Segoe UI Semibold", 15))
            using (var fS = SF.New("Segoe UI", 13))
            using (var center = new StringFormat())
            using (var ell = new StringFormat())
            {
                center.Alignment = StringAlignment.Center;
                ell.Trimming = StringTrimming.EllipsisCharacter; ell.FormatFlags = StringFormatFlags.NoWrap;

                // clima
                if (!Widgets.HasWeather)
                {
                    string msg = Widgets.WeatherError != "" ? Widgets.WeatherError : Widgets.City.Trim() == "" ? "Defina a cidade no app\n(aba Painel)" : "Carregando clima...";
                    using (var c = new StringFormat()) { c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                        g.DrawString(msg, fS, dim, wr, c); }
                }
                else
                {
                    Widgets.DrawIcon(g, Widgets.Code, Widgets.IsDay, new RectangleF(wr.X + 8, wr.Y + 6, 72, 72), accent, fgColor);
                    g.DrawString(Widgets.Temp.ToString("0") + "°", fTemp, fg, wr.X + 84, wr.Y + 2);
                    g.DrawString(Widgets.Describe(Widgets.Code), fM, acc, new RectangleF(wr.X + 88, wr.Y + 62, wr.Width - 92, 20), ell);
                    g.DrawString(Widgets.Place, fM, fg, new RectangleF(wr.X + 10, wr.Y + 88, wr.Width - 20, 20), ell);
                    g.DrawString("↓" + Widgets.TMin.ToString("0") + "°  ↑" + Widgets.TMax.ToString("0") + "°   Sens. " + Widgets.Feels.ToString("0") + "°   " + Widgets.Humidity.ToString("0") + "% umid.",
                        fS, dim, new RectangleF(wr.X + 10, wr.Y + 108, wr.Width - 20, 18), ell);
                    var hours = Widgets.Hours;
                    int n = Math.Min(hours.Count, wr.Width >= 300 ? 6 : 4);
                    int hy = wr.Y + 132, hh = wr.Bottom - hy - 4;
                    if (n > 0 && hh >= 60)
                    {
                        int cw = (wr.Width - 12) / n;
                        for (int i = 0; i < n; i++)
                        {
                            int cx = wr.X + 6 + i * cw;
                            g.DrawString(hours[i].Time.ToString("HH") + "h", fS, dim, new RectangleF(cx, hy, cw, 18), center);
                            Widgets.DrawIcon(g, hours[i].Code, hours[i].Time.Hour >= 6 && hours[i].Time.Hour < 18, new RectangleF(cx + cw / 2 - 15, hy + 18, 30, 30), accent, fgColor);
                            g.DrawString(hours[i].Temp.ToString("0") + "°", fM, fg, new RectangleF(cx, hy + 48, cw, 20), center);
                        }
                    }
                }
            }
        }

        void DrawQuotesBox(Graphics g, Rectangle qr)
        {
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(170, fgColor)))
            using (var acc = new SolidBrush(accent))
            using (var fS = SF.New("Segoe UI", 13))
            using (var fQ = SF.New("Segoe UI Semibold", 20))
            using (var right = new StringFormat())
            using (var ell = new StringFormat())
            {
                right.Alignment = StringAlignment.Far;
                ell.Trimming = StringTrimming.EllipsisCharacter; ell.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString("COTAÇÕES", fS, acc, qr.X + 10, qr.Y + 8);
                var qs = Widgets.Quotes;
                if (qs.Count == 0)
                {
                    using (var c = new StringFormat()) { c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                        g.DrawString(Widgets.QuoteError != "" ? Widgets.QuoteError : "Carregando...", fS, dim, qr, c); }
                }
                else
                {
                    int ry = qr.Y + 30, rh = Math.Min(52, (qr.Bottom - ry - 6) / qs.Count);
                    using (var up = new SolidBrush(Color.FromArgb(60, 200, 110))) using (var down = new SolidBrush(Color.FromArgb(240, 80, 80)))
                        for (int i = 0; i < qs.Count; i++)
                        {
                            var q = qs[i]; int y = ry + i * rh;
                            string sym = q.In == "BRL" ? "R$ " : q.In == "USD" ? "US$ " : q.In + " ";
                            string val = sym + q.Bid.ToString(q.Bid >= 1000 ? "N0" : "N2", PT);
                            g.DrawString(q.Code, fS, dim, qr.X + 10, y + 2);
                            g.DrawString(val, fQ, fg, new RectangleF(qr.X + 6, y + 16, qr.Width - 80, 26), ell);
                            string pct = (q.Pct >= 0 ? "▲ " : "▼ ") + Math.Abs(q.Pct).ToString("0.00", PT) + "%";
                            g.DrawString(pct, fS, q.Pct >= 0 ? up : down, new RectangleF(qr.X, y + 22, qr.Width - 10, 20), right);
                        }
                }
            }
        }

        void DrawCards(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            var now = DateTime.Now;
            var pt = new CultureInfo("pt-BR");
            using (var fg = new SolidBrush(fgColor))
            using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
            using (var acc = new SolidBrush(accent))
            using (var fTime = SF.New("Segoe UI Light", h > w ? 54 : 50))
            using (var fSmall = SF.New("Segoe UI", 15))
            using (var fLabel = SF.New("Segoe UI Semibold", 13))
            using (var fVal = SF.New("Segoe UI Semibold", 24))
            {
                string time = now.ToString(clock24 ? "HH:mm:ss" : "hh:mm:ss tt", pt);
                g.DrawString(time, fTime, fg, 14, 6);
                string date = DateStr();
                g.DrawString(date, fSmall, dim, 18, 66);
                var tsz = g.MeasureString(title, fSmall);
                bool land = w > h;
                if (land) g.DrawString(title, fSmall, acc, w - tsz.Width - 14, 66);
                else g.DrawString(title, fSmall, acc, 18, 86);
                int top = land ? 98 : 116;

                // especificacoes no cabecalho (ao lado da hora)
                if (specMode == 2 || specMode == 3)
                    using (var fSpec = SF.New("Segoe UI", 13))
                    using (var sf = new StringFormat())
                    {
                        sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap;
                        if (land)
                        {
                            sf.Alignment = StringAlignment.Far;
                            float sx = g.MeasureString(time, fTime).Width + 20;
                            g.DrawString(Specs.Line1(), fSpec, dim, new RectangleF(sx, 16, w - sx - 14, 18), sf);
                            g.DrawString(Specs.Line2(), fSpec, dim, new RectangleF(sx, 34, w - sx - 14, 18), sf);
                        }
                        else
                        {
                            g.DrawString(Specs.Line1(), fSpec, dim, new RectangleF(18, 108, w - 30, 18), sf);
                            g.DrawString(Specs.Line2(), fSpec, dim, new RectangleF(18, 126, w - 30, 18), sf);
                            top = 146;
                        }
                    }

                var cards = new List<CardData>();
                foreach (var id in cardOrder)
                    if (cardOn.Contains(id)) { var c = BuildCard(id, specMode == 1 || specMode == 3); if (c != null) cards.Add(c); }

                int cols = land ? 3 : 2, pad = 10;
                int rows = (cards.Count + cols - 1) / cols;
                if (rows == 0) return;
                int cw = (w - pad * (cols + 1)) / cols, ch = Math.Min(110, (h - top - pad * (rows + 1)) / rows);
                bool compact = ch < 80;   // 3 linhas de cartoes: sem subtitulo, fontes menores
                using (var fValS = SF.New("Segoe UI Semibold", 20))
                using (var fLabelS = SF.New("Segoe UI Semibold", 11))
                using (var fSub = SF.New("Segoe UI", 12))
                using (var ell = new StringFormat())
                using (var cardBg = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                using (var barBg = new SolidBrush(Color.FromArgb(50, fgColor)))
                {
                    ell.Trimming = StringTrimming.EllipsisCharacter; ell.FormatFlags = StringFormatFlags.NoWrap;
                    for (int i = 0; i < cards.Count; i++)
                    {
                        var c = cards[i];
                        int cx = pad + (i % cols) * (cw + pad), cy = top + pad + (i / cols) * (ch + pad);
                        using (var path = Round(new Rectangle(cx, cy, cw, ch), 10)) g.FillPath(cardBg, path);
                        Color barColor = CardColor(c, c.Pct);
                        using (var bb = new SolidBrush(barColor))
                        {
                            if (compact)
                            {
                                g.DrawString(c.Label, fLabelS, acc, new RectangleF(cx + 8, cy + 5, cw - 12, 15), ell);
                                g.DrawString(c.Value, fValS, fg, new RectangleF(cx + 7, cy + 20, cw - 10, 26), ell);
                            }
                            else
                            {
                                g.DrawString(c.Label, fLabel, acc, new RectangleF(cx + 10, cy + 8, cw - 14, 17), ell);
                                g.DrawString(c.Value, fVal, fg, new RectangleF(cx + 9, cy + 25, cw - 12, 32), ell);
                                if (c.Sub != "" && ch >= 84) g.DrawString(c.Sub, fSub, dim, new RectangleF(cx + 10, cy + 56, cw - 14, 16), ell);
                            }
                            if (c.Pct >= 0)
                            {
                                var bar = compact ? new Rectangle(cx + 8, cy + ch - 10, cw - 16, 4) : new Rectangle(cx + 10, cy + ch - 16, cw - 20, 6);
                                g.FillRectangle(barBg, bar);
                                bar.Width = (int)(bar.Width * Math.Min(100, c.Pct) / 100);
                                if (bar.Width > 0) g.FillRectangle(bb, bar);
                            }
                        }
                    }
                }
            }
        }

        // ---------------- cartoes configuraveis
        class CardData { public string Label = "", Value = "", Sub = ""; public double Pct = -1; public bool Temp, Bat; }

        static readonly string[] CardIds = { "cpu", "ram", "gpu", "cputemp", "gputemp", "disk", "down", "up",
            "cpuclock", "gpuclock", "cpupower", "gpupower", "fan", "gpufan", "ssdtemp", "meeting",
            "batmouse", "batkeyboard", "batheadset", "batgamepad", "vpn" };
        static readonly string[] CardNames = { "CPU (uso)", "Memória RAM", "GPU (uso)", "Temperatura da CPU", "Temperatura da GPU", "Disco C:", "Download", "Upload",
            "Clock da CPU", "Clock da GPU", "Consumo da CPU", "Consumo da GPU", "Ventoinhas do gabinete", "Ventoinha da GPU", "Temperatura do SSD", "Próxima reunião",
            "Bateria do mouse", "Bateria do teclado", "Bateria do fone", "Bateria do controle", "VPN conectada" };
        bool batWarn = true;

        // ---------------- VPN
        bool vpnStrip = true, vpnToast = true, vpnHide;
        string vpnShown = "";

        void TickVpn()
        {
            var l = Vpn.List;
            string text = "";
            foreach (var c in l) text += (text != "" ? "  +  " : "") + Vpn.Describe(c, vpnHide);
            if (vpnStrip && text != "" && !(lastEffMode == 0 && lastEffTheme == VpnTheme)) // no tema VPN a faixa repetiria
            {
                // prioridade minima: o Claude Code e a reuniao passam na frente
                var i = new StatusItem { Key = "vpn", Label = "VPN", State = "info", Text = text, Expires = DateTime.Now.AddSeconds(30) };
                if (text != vpnShown || DateTime.Now.Second % 10 == 0) StatusBoard.Set(i, false);
                vpnShown = text;
            }
            else if (vpnShown != "") { StatusBoard.Remove("vpn"); vpnShown = ""; }
        }

        // tema VPN
        void DrawVpnTheme(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            int top;
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor))) using (var acc = new SolidBrush(accent))
                top = DrawHeader(g, w, fg, dim, acc);
            DrawVpnBox(g, new Rectangle(8, top + 6, w - 16, h - top - 14 - stripInset), true, false, 0);
        }

        static readonly Color VpnGreen = Color.FromArgb(76, 199, 120);

        // uma linha por VPN conectada: nome do cliente, perfil, tempo conectada e trafego
        void DrawVpnBox(Graphics g, Rectangle box, bool rowPanels, bool label, int font)
        {
            var l = Vpn.List;
            using (var fg = new SolidBrush(fgColor)) using (var dim = new SolidBrush(Color.FromArgb(160, fgColor)))
            using (var acc = new SolidBrush(accent)) using (var panel = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var green = new SolidBrush(VpnGreen))
            using (var near = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                if (label && box.Height >= 40)
                    using (var f = SF.New("Segoe UI Semibold", Math.Max(9, Math.Min(13, box.Height / 6))))
                    {
                        g.DrawString("VPN", f, acc, new RectangleF(box.X + 7, box.Y + 4, box.Width - 14, f.Height), near);
                        box = new Rectangle(box.X, box.Y + f.Height + 6, box.Width, box.Height - f.Height - 6);
                    }
                if (l.Count == 0)
                {
                    int px = font > 0 ? font : Math.Max(11, Math.Min(22, box.Height / 6));
                    using (var f = SF.New("Segoe UI", px))
                    using (var c = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    using (var off = new SolidBrush(Color.FromArgb(90, fgColor)))
                    {
                        string s = "Nenhuma VPN conectada";
                        var sz = g.MeasureString(s, f);
                        float dot = px * 0.6f, total = dot + 8 + sz.Width, x0 = box.X + Math.Max(0, (box.Width - total) / 2), cy = box.Y + box.Height / 2f;
                        g.FillEllipse(off, x0, cy - dot / 2, dot, dot);
                        g.DrawString(s, f, dim, new RectangleF(x0 + dot + 8, box.Y, box.Width - (x0 - box.X) - dot - 8, box.Height), near);
                    }
                    return;
                }
                int gap = rowPanels ? 8 : 4, n = Math.Min(l.Count, Math.Max(1, (box.Height + gap) / (30 + gap)));
                int rh = Math.Min(rowPanels ? 120 : 90, (box.Height - gap * (n - 1)) / n);
                for (int i = 0; i < n; i++)
                {
                    var c = l[i];
                    var r = new Rectangle(box.X, box.Y + i * (rh + gap), box.Width, rh);
                    if (rowPanels) using (var p = Round(r, 8)) g.FillPath(panel, p);
                    bool big = rh >= 56;
                    int pxName = font > 0 ? font : Math.Max(12, Math.Min(26, rh * 26 / 100));
                    int pxSub = Math.Max(10, Math.Min(16, pxName * 6 / 10)), pxDet = Math.Max(10, Math.Min(13, pxName / 2));
                    string main = Vpn.Place(c, vpnHide), prof = Vpn.Program(c, vpnHide); // perfil em destaque, programa embaixo
                    string det = "conectada há " + Vpn.Duration(c);
                    if (c.Down >= 0) det += "   ↓ " + SysInfo.Rate(c.Down) + "   ↑ " + SysInfo.Rate(c.Up);
                    using (var fN = SF.New("Segoe UI Semibold", pxName))
                    using (var fS = SF.New("Segoe UI", pxSub))
                    using (var fD = SF.New("Segoe UI", pxDet))
                    {
                        int pad = rowPanels ? 12 : 7;
                        float dot = Math.Max(7, pxName * 0.45f);
                        int tx = r.X + pad + (int)dot + 8, tw = Math.Max(1, r.Right - pad - tx);
                        if (big)
                        {
                            int lines = prof != "" ? 3 : 2;
                            float hN = fN.Height, hS = prof != "" ? fS.Height : 0, hD = fD.Height, tot = hN + hS + hD + 4 * (lines - 1);
                            float y = r.Y + Math.Max(2, (r.Height - tot) / 2);
                            g.FillEllipse(green, r.X + pad, y + hN / 2 - dot / 2, dot, dot);
                            g.DrawString(main, fN, fg, new RectangleF(tx, y, tw, hN), near); y += hN + 4;
                            if (prof != "") { g.DrawString(prof, fS, acc, new RectangleF(tx, y, tw, hS), near); y += hS + 4; }
                            g.DrawString(det, fD, dim, new RectangleF(tx, y, tw, hD), near);
                        }
                        else
                        {
                            g.FillEllipse(green, r.X + pad, r.Y + r.Height / 2f - dot / 2, dot, dot);
                            float nw = g.MeasureString(main, fN).Width;
                            g.DrawString(main, fN, fg, new RectangleF(tx, r.Y, tw, r.Height), near);
                            string rest = (prof != "" ? prof + " · " : "") + Vpn.Duration(c);
                            g.DrawString(rest, fS, dim, new RectangleF(tx + nw + 2, r.Y, Math.Max(1, tw - nw - 2), r.Height), near);
                        }
                    }
                }
            }
        }

        string VpnText()
        {
            var l = Vpn.List;
            if (l.Count == 0) return "VPN desligada";
            string s = ""; foreach (var c in l) s += (s != "" ? " + " : "") + Vpn.Describe(c, vpnHide);
            return s;
        }

        CardData VpnCard()
        {
            var l = Vpn.List;
            var c = new CardData { Label = "VPN" };
            if (l.Count == 0) { c.Value = "desligada"; return c; }
            c.Value = Vpn.Place(l[0], vpnHide);
            string sec = Vpn.Program(l[0], vpnHide);
            c.Sub = sec + (l.Count > 1 ? (sec != "" ? " " : "") + "(+" + (l.Count - 1) + ")" : "");
            return c;
        }

        static Color BatColor(double p)
        {
            if (p <= 20) return Color.FromArgb(239, 83, 80);
            if (p <= 40) return Color.FromArgb(255, 167, 38);
            return Color.FromArgb(76, 199, 120);
        }

        Color CardColor(CardData c, double v)
        {
            if (c.Temp) return TempColor(v);
            if (c.Bat) return BatColor(v);
            return accent;
        }

        static CardData BatteryCard(string kind)
        {
            var d = Batteries.Lowest(kind);
            if (d == null) return null;
            int n = 0; foreach (var x in Batteries.List) if (x.Kind == kind) n++;
            var c = new CardData { Label = Batteries.KindName(kind).ToUpperInvariant(), Bat = true };
            if (d.Percent >= 0) { c.Value = d.Percent + "%"; c.Pct = d.Percent; }
            else c.Value = d.Charging ? "carregando" : "—";
            c.Sub = d.Name + (n > 1 ? " (+" + (n - 1) + ")" : "");
            if (d.Charging && d.Percent >= 0) c.Sub = "⚡ " + c.Sub;
            else if (d.Asleep) c.Sub += " · dormindo";
            return c;
        }
        volatile List<string> cardOrder = new List<string>(CardIds);
        volatile HashSet<string> cardOn = new HashSet<string>(new[] { "cpu", "ram", "gpu", "cputemp", "gputemp", "disk" });
        int specMode = 2; // 0 nao mostrar, 1 nos cartoes, 2 no cabecalho, 3 os dois

        bool showGpu { get { return cardOn.Contains("gpu"); } }
        bool showNet { get { return cardOn.Contains("down") || cardOn.Contains("up"); } }
        bool showDisk { get { return cardOn.Contains("disk"); } }

        static Color TempColor(double t)
        {
            if (t >= 85) return Color.FromArgb(239, 83, 80);
            if (t >= 70) return Color.FromArgb(255, 167, 38);
            return Color.FromArgb(76, 199, 120);
        }

        CardData BuildCard(string id, bool spec)
        {
            var c = new CardData();
            switch (id)
            {
                case "cpu":
                    c.Label = "CPU"; c.Value = SysInfo.Cpu.ToString("0") + "%"; c.Pct = SysInfo.Cpu;
                    if (spec) c.Sub = Specs.Cpu; break;
                case "ram":
                    c.Label = "RAM"; c.Value = SysInfo.RamUsedGb.ToString("0.0") + "/" + SysInfo.RamTotalGb.ToString("0") + " GB"; c.Pct = SysInfo.RamPct;
                    if (spec) c.Sub = Specs.RamType; break;
                case "gpu":
                    if (SysInfo.GpuPct < 0) return null;
                    c.Label = "GPU"; c.Value = SysInfo.GpuPct.ToString("0") + "%"; c.Pct = SysInfo.GpuPct;
                    if (spec) c.Sub = Specs.Gpu; break;
                case "cputemp":
                    c.Label = "TEMP. CPU"; c.Temp = true;
                    if (CpuTemp.Value >= 0) { c.Value = CpuTemp.Value.ToString("0") + " °C"; c.Pct = CpuTemp.Value; }
                    else { c.Value = "—"; c.Sub = Process.GetProcessesByName("LibreHardwareMonitor").Length > 0 ? "ligue o Web Server" : "requer LibreHWMonitor"; }
                    break;
                case "gputemp":
                    if (SysInfo.GpuTemp < 0) return null;
                    c.Label = "TEMP. GPU"; c.Temp = true; c.Value = SysInfo.GpuTemp.ToString("0") + " °C"; c.Pct = SysInfo.GpuTemp; break;
                case "disk":
                    c.Label = "DISCO C:"; c.Value = SysInfo.DiskPct.ToString("0") + "%"; c.Pct = SysInfo.DiskPct;
                    c.Sub = spec && Specs.Disk != "" ? SysInfo.DiskFreeGb.ToString("0") + " / " + Specs.Disk + " livres" : SysInfo.DiskFreeGb.ToString("0") + " GB livres"; break;
                case "down": c.Label = "DOWNLOAD"; c.Value = SysInfo.Rate(SysInfo.NetDown); break;
                case "up": c.Label = "UPLOAD"; c.Value = SysInfo.Rate(SysInfo.NetUp); break;
                case "meeting":
                    var nextMeet = Agenda.Next();
                    c.Label = "REUNIÃO";
                    if (nextMeet == null) { c.Value = "—"; c.Sub = "sem reuniões"; break; }
                    c.Value = MeetingWhen(nextMeet); c.Sub = MeetingTitle(nextMeet); break;
                case "cpuclock":
                    if (Lhm.CpuClockGhz < 0) return null;
                    c.Label = "CLOCK CPU"; c.Value = Lhm.CpuClockGhz.ToString("0.0", PT) + " GHz"; break;
                case "gpuclock":
                    if (Lhm.GpuClock < 0) return null;
                    c.Label = "CLOCK GPU"; c.Value = Lhm.GpuClock.ToString("0") + " MHz"; break;
                case "cpupower":
                    if (Lhm.CpuPower < 0) return null;
                    c.Label = "CONSUMO CPU"; c.Value = Lhm.CpuPower.ToString("0") + " W"; break;
                case "gpupower":
                    SysInfo.NeedGpuPowerUntil = DateTime.Now.AddSeconds(5);
                    double gw = Lhm.GpuPower >= 0 ? Lhm.GpuPower : SysInfo.GpuPowerSmi;
                    c.Label = "CONSUMO GPU"; c.Value = gw >= 0 ? gw.ToString("0") + " W" : "—";
                    if (gw < 0 && SysInfo.GpuPowerUnsupported) c.Sub = "driver não informa";
                    break;
                case "fan":
                    if (Lhm.CaseFan < 0) return null;
                    c.Label = "VENTOINHAS"; c.Value = Lhm.CaseFan.ToString("0") + " RPM"; break;
                case "gpufan":
                    if (Lhm.GpuFan < 0) return null;
                    c.Label = "FAN GPU"; c.Value = Lhm.GpuFan.ToString("0") + " RPM"; if (Lhm.GpuFan == 0) c.Sub = "parada (GPU fria)"; break;
                case "ssdtemp":
                    if (Lhm.SsdTemp < 0) return null;
                    c.Label = "TEMP. SSD"; c.Temp = true; c.Value = Lhm.SsdTemp.ToString("0") + " °C"; c.Pct = Lhm.SsdTemp; break;
                case "batmouse": return BatteryCard("mouse");
                case "batkeyboard": return BatteryCard("keyboard");
                case "batheadset": return BatteryCard("headset");
                case "batgamepad": return BatteryCard("gamepad");
                case "vpn": return VpnCard();
                default: return null;
            }
            return c;
        }

        void LoadCards()
        {
            string s = cfg.Get("cards", "");
            if (s == "")
            {
                // migra as opcoes antigas (gpu/rede/disco)
                var on = new HashSet<string>(new[] { "cpu", "ram", "cputemp", "gputemp" });
                if (cfg.GetBool("gpu", true)) on.Add("gpu");
                if (cfg.GetBool("disk", true)) on.Add("disk");
                if (cfg.GetBool("net", false)) { on.Add("down"); on.Add("up"); }
                cardOn = on; return;
            }
            var order = new List<string>(); var set = new HashSet<string>();
            foreach (var part in s.Split(','))
            {
                var kv = part.Split(':');
                if (Array.IndexOf(CardIds, kv[0]) < 0 || order.Contains(kv[0])) continue;
                order.Add(kv[0]); if (kv.Length > 1 && kv[1] == "1") set.Add(kv[0]);
            }
            foreach (var id in CardIds) if (!order.Contains(id)) order.Add(id); // cartoes novos de versoes futuras
            cardOrder = order; cardOn = set;
        }

        string SaveCards()
        {
            var l = new List<string>();
            foreach (var id in cardOrder) l.Add(id + ":" + (cardOn.Contains(id) ? "1" : "0"));
            return string.Join(",", l.ToArray());
        }

        void BuildCardsTab(TabPage tc)
        {
            L("Marque os cartões e escolha a ordem:", 8, 6, tc);
            var list = Add(new CheckedListBox(), 10, 30, 230, tc); list.Height = 128; list.CheckOnClick = true; list.IntegralHeight = false;
            var chkBat = Add(new CheckBox(), 10, 162, 330, tc);
            chkBat.Text = "Avisar quando um periférico ficar com " + Batteries.LowAt + "% de bateria";
            chkBat.Checked = batWarn;
            chkBat.CheckedChanged += delegate { batWarn = chkBat.Checked; };
            Action fill = delegate
            {
                list.Items.Clear();
                foreach (var id in cardOrder) list.Items.Add(CardNames[Array.IndexOf(CardIds, id)], cardOn.Contains(id));
            };
            fill();
            Action commit = delegate
            {
                var order = new List<string>(); var on = new HashSet<string>();
                for (int i = 0; i < list.Items.Count; i++)
                {
                    string id = CardIds[Array.IndexOf(CardNames, (string)list.Items[i])];
                    order.Add(id); if (list.GetItemChecked(i)) on.Add(id);
                }
                cardOrder = order; cardOn = on; lcd.Invalidate();
            };
            list.ItemCheck += (s, e) => BeginInvoke(commit);
            Action<int> move = dir =>
            {
                int i = list.SelectedIndex, j = i + dir;
                if (i < 0 || j < 0 || j >= list.Items.Count) return;
                object item = list.Items[i]; bool chk = list.GetItemChecked(i);
                list.Items.RemoveAt(i); list.Items.Insert(j, item); list.SetItemChecked(j, chk);
                list.SelectedIndex = j; commit();
            };
            Btn("▲ Subir", 248, 30, 88, tc, delegate { move(-1); });
            Btn("▼ Descer", 248, 62, 88, tc, delegate { move(1); });
            var hint = L("Até 6 cabem\ncom folga; 7+\nficam\ncompactos.", 248, 94, tc);
            hint.ForeColor = Color.Gray;

            L("Especificações da máquina:", 8, 192, tc);
            var cbSpec = Add(new ComboBox(), 160, 190, 176, tc); cbSpec.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSpec.Items.AddRange(new object[] { "Não mostrar", "Dentro dos cartões", "Ao lado da hora", "Hora e cartões" });
            cbSpec.SelectedIndex = Math.Max(0, Math.Min(3, specMode));
            cbSpec.SelectedIndexChanged += delegate { specMode = cbSpec.SelectedIndex; lcd.Invalidate(); };

            var lblSpecs = (Label)L("Detectando hardware...", 8, 220, tc); lblSpecs.ForeColor = Color.DimGray;
            var t = new System.Windows.Forms.Timer(); t.Interval = 1500; t.Start();
            t.Tick += delegate
            {
                lblSpecs.Text = Specs.Cpu + "  ·  " + Specs.CpuCores + "\n" + Specs.Gpu + " " + Specs.GpuMem + "  ·  " + Specs.Ram + " " + Specs.RamType +
                    "\n" + Specs.Board + "  ·  " + Specs.Os +
                    "\nTemp. CPU: " + (CpuTemp.Value >= 0 ? CpuTemp.Value.ToString("0") + " °C (" + CpuTemp.Source + ")" : "requer LibreHardwareMonitor aberto");
            };
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        int DrawPhotoFrame(Graphics g, int w, int h)
        {
            PhotoFrame.Want(w, h, imageFill);
            var f = PhotoFrame.Current;
            g.Clear(Color.Black);
            if (f == null || f.W != w || f.H != h)
            {
                using (var ft = SF.New("Segoe UI", 16)) using (var c = new StringFormat())
                {
                    c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                    g.DrawString(PhotoFrame.Status != "" ? PhotoFrame.Status : "Carregando fotos...", ft, Brushes.Gray, new RectangleF(0, 0, w, h), c);
                }
                return 500;
            }
            g.DrawImageUnscaled(f.Image, 0, 0);
            if (PhotoFrame.ShowClock)
            {
                int bottom = h - stripInset, gh = 78;
                var r = new Rectangle(0, bottom - gh, w, gh);
                using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(185, 0, 0, 0), 90f)) g.FillRectangle(b, r);
                using (var fTime = SF.New("Segoe UI Light", 40))
                using (var fDate = SF.New("Segoe UI", 14))
                using (var right = new StringFormat())
                {
                    right.Alignment = StringAlignment.Far;
                    g.DrawString(TimeStr(false), fTime, Brushes.White, 12, bottom - 56);
                    g.DrawString(DateStr(), fDate, Brushes.White, new RectangleF(0, bottom - 42, w - 12, 18), right);
                    if (f.Caption != "") using (var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                        g.DrawString("Foto de " + f.Caption, fDate, dim, new RectangleF(0, bottom - 24, w - 12, 18), right);
                }
            }
            return 1000 - DateTime.Now.Millisecond + 5;
        }

        int DrawImage(Graphics g, int w, int h)
        {
            if (PhotoFrame.Enabled && PhotoFrame.Folder.Trim() != "") return DrawPhotoFrame(g, w, h);
            g.Clear(Color.Black);
            lock (imgLock)
            {
                if (image == null)
                {
                    using (var f = SF.New("Segoe UI", 18)) g.DrawString("Escolha uma imagem no app", f, Brushes.Gray, 20, h / 2 - 12);
                    return 500;
                }
                int delay = 1000;
                if (gifFrames > 1)
                {
                    image.SelectActiveFrame(gifDim, gifFrame);
                    delay = gifDelays[gifFrame];
                    gifFrame = (gifFrame + 1) % gifFrames;
                }
                DrawCover(g, image, w, h, imageFill);
                return delay;
            }
        }

        void DrawMirror(Graphics g, int w, int h)
        {
            Rectangle src;
            if (mirrorFollowMouse)
            {
                var c = Cursor.Position;
                src = new Rectangle(c.X - w / 2, c.Y - h / 2, w, h);
            }
            else
            {
                var scr = Screen.AllScreens[Math.Min(mirrorScreen, Screen.AllScreens.Length - 1)];
                src = scr.Bounds;
            }
            using (var cap = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
            {
                using (var cg = Graphics.FromImage(cap)) cg.CopyFromScreen(src.Location, Point.Empty, src.Size);
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                DrawCover(g, cap, w, h, false);
            }
            if (mirrorFollowMouse) { g.DrawLine(Pens.Red, w / 2 - 6, h / 2, w / 2 + 6, h / 2); g.DrawLine(Pens.Red, w / 2, h / 2 - 6, w / 2, h / 2 + 6); }
        }

        bool IsIgnored(string app)
        {
            foreach (var s in (notifIgnore ?? "").Split(','))
            {
                var p = s.Trim();
                if (p != "" && app.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static bool WindowsLightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        GroupBox grpOnAir;
        int curToastSeconds = 8;
        int stripInset; // altura ocupada pela faixa de status neste quadro

        // Faixa fina no rodape: Claude Code e outros status enviados pela API
        void DrawStatusStrip(Graphics g, int w, int h)
        {
            int total;
            var it = StatusBoard.Top(out total);
            if (it == null) return;
            bool waiting = it.State == "waiting", error = it.State == "error";
            Color back = waiting ? Color.FromArgb(255, 179, 0) : error ? Color.FromArgb(198, 40, 40) : Color.FromArgb(24, 24, 28);
            Color fore = waiting ? Color.FromArgb(20, 20, 20) : Color.White;
            Color dot = it.State == "working" ? accent : it.State == "done" || it.Key == "vpn" ? Color.FromArgb(76, 199, 120) : fore;
            var r = new Rectangle(0, h - 22, w, 22);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, r);
            int x = 10;
            using (var d = new SolidBrush(dot))
            {
                if (it.State == "working")
                {
                    // tres pontos andando: muda poucos pixels por segundo
                    int on = DateTime.Now.Second % 3;
                    for (int i = 0; i < 3; i++)
                        using (var p = new SolidBrush(Color.FromArgb(i == on ? 255 : 70, dot))) g.FillEllipse(p, x + i * 9, r.Y + 8, 6, 6);
                    x += 30;
                }
                else { g.FillEllipse(d, x, r.Y + 7, 8, 8); x += 15; }
            }
            var parts = new List<string>();
            foreach (var s in new[] { it.Label, it.Text, it.Detail }) if (s != "") parts.Add(s.Replace('\n', ' '));
            string extra = total > 1 ? "+" + (total - 1) : "";
            using (var f = SF.New("Segoe UI Semibold", 13))
            using (var fb = new SolidBrush(fore))
            using (var sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap; sf.LineAlignment = StringAlignment.Center;
                int right = w - 8;
                if (extra != "")
                {
                    int ew = (int)g.MeasureString(extra, f).Width + 2;
                    right -= ew;
                    g.DrawString(extra, f, fb, new RectangleF(right, r.Y, ew, r.Height), sf);
                    right -= 6;
                }
                g.DrawString(string.Join("  ·  ", parts.ToArray()), f, fb, new RectangleF(x, r.Y, right - x, r.Height), sf);
            }
        }

        static string OnAirText()
        {
            if (OnAir.Testing) return "MICROFONE LIGADO  ·  Teste";
            bool m = OnAir.ShowMic, c = OnAir.ShowCam;
            if (!m && !c) return "";
            string what = m && c ? "MICROFONE E CÂMERA" : m ? "MICROFONE LIGADO" : "CÂMERA LIGADA";
            string app = m ? OnAir.MicApp : OnAir.CamApp;
            return app != "" ? what + "  ·  " + app : what;
        }

        // Moldura vermelha + etiqueta no canto: visivel de longe sem cobrir o relogio
        void DrawOnAir(Graphics g, int w, int h)
        {
            string text = OnAirText();
            if (text == "") return;
            Color red = Color.FromArgb(217, 45, 32);
            using (var pen = new Pen(red, 4)) g.DrawRectangle(pen, 2, 2, w - 5, h - 5);
            using (var f = SF.New("Segoe UI Semibold", 13))
            using (var sf = new StringFormat())
            using (var bg = new SolidBrush(red))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap; sf.LineAlignment = StringAlignment.Center;
                int tw = Math.Min((int)g.MeasureString(text, f).Width + 2, w - 54);
                var r = new Rectangle(w - tw - 36, 8, tw + 28, 24);
                using (var p = Round(r, 12)) g.FillPath(bg, p);
                g.FillEllipse(Brushes.White, r.X + 9, r.Y + 8, 8, 8);
                g.DrawString(text, f, Brushes.White, new RectangleF(r.X + 21, r.Y, tw + 2, r.Height), sf);
            }
        }

        // Cartao no estilo da notificacao do Windows 11
        void DrawToast(Graphics g, int w, int h, ToastInfo t)
        {
            bool light = WindowsLightTheme();
            Color card = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
            Color border = light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(70, 70, 70);
            Color main = light ? Color.FromArgb(26, 26, 26) : Color.White;
            Color sub = light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(200, 200, 200);

            int m = 8, cw = w - m * 2, pad = 14;
            string title = t.Title != "" ? t.Title : t.App;
            string body = notifHideBody ? "Nova mensagem" : t.Body;
            using (var fApp = SF.New("Segoe UI", 13))
            using (var fTitle = SF.New("Segoe UI Semibold", 19))
            using (var fBody = SF.New("Segoe UI", 17))
            using (var sfTitle = new StringFormat())
            using (var sfBody = new StringFormat())
            {
                sfTitle.Trimming = StringTrimming.EllipsisCharacter; sfTitle.FormatFlags = StringFormatFlags.NoWrap;
                sfBody.Trimming = StringTrimming.EllipsisWord;
                int tw = cw - pad * 2;
                int titleH = 26;
                int maxBody = h - m * 2 - (pad + 22 + 10 + titleH + 4 + pad);
                int bodyH = body == "" ? 0 : (int)Math.Ceiling(g.MeasureString(body, fBody, tw, sfBody).Height);
                bodyH = Math.Min(bodyH, maxBody);
                int ch = pad + 22 + 10 + titleH + (bodyH > 0 ? 4 + bodyH : 0) + pad;
                var r = new Rectangle(m, h - m - ch, cw, ch);

                using (var shadow = Round(new Rectangle(r.X + 2, r.Y + 4, r.Width, r.Height), 10))
                using (var sb = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillPath(sb, shadow);
                using (var p = Round(r, 10))
                using (var b = new SolidBrush(card))
                using (var pen = new Pen(border))
                { g.FillPath(b, p); g.DrawPath(pen, p); }

                int x = r.X + pad, y = r.Y + pad;
                if (t.Icon != null) g.DrawImage(t.Icon, x, y + 1, 20, 20);
                else using (var ib = new SolidBrush(accent)) g.FillEllipse(ib, x + 2, y + 3, 16, 16);
                using (var sb = new SolidBrush(sub))
                {
                    string when = (DateTime.Now - t.Time).TotalMinutes < 1 ? "agora" : t.Time.ToString("HH:mm");
                    var ws = g.MeasureString(when, fApp);
                    g.DrawString(when, fApp, sb, r.Right - pad - ws.Width, y + 2);
                    g.DrawString(t.App, fApp, sb, new RectangleF(x + 28, y + 2, tw - 28 - ws.Width - 8, 20), sfTitle);
                    y += 22 + 10;
                    using (var mb = new SolidBrush(main)) g.DrawString(title, fTitle, mb, new RectangleF(x, y, tw, titleH), sfTitle);
                    y += titleH + 4;
                    if (bodyH > 0) g.DrawString(body, fBody, sb, new RectangleF(x, y, tw, bodyH), sfBody);
                }

                // barra de tempo restante
                double left = Math.Max(0, (toastUntil - DateTime.Now).TotalSeconds / Math.Max(1, curToastSeconds));
                using (var ab = new SolidBrush(accent)) g.FillRectangle(ab, r.X + 10, r.Bottom - 3, (int)((r.Width - 20) * left), 2);
            }
        }

        void DrawText(Graphics g, int w, int h)
        {
            DrawBackground(g, w, h);
            using (var f = SF.New("Segoe UI Semibold", textSize))
            using (var b = new SolidBrush(fgColor))
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(textContent, f, b, new RectangleF(8, 8, w - 16, h - 16), sf);
            }
        }

        [STAThread]
        static void Main()
        {
            var cmd = Environment.GetCommandLineArgs();
            if (cmd.Length > 1 && cmd[1] == "/claude-hook") { ClaudeHook.Run(); return; } // nao abre janela
            if (cmd.Length > 3 && cmd[1] == "/icstest") { Agenda.Dump(cmd[2], cmd[3]); return; }

            bool created;
            using (var mtx = new Mutex(true, "TurzxScreenSingleInstance", out created))
            {
                if (!created) { MessageBox.Show("O TURZX Screen ja esta aberto (veja na bandeja)."); return; }
                Application.ThreadException += (s, e) => Log.Error("interface", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { var ex = e.ExceptionObject as Exception; if (ex != null) Log.Error("fatal", ex); };
                Application.EnableVisualStyles();
                Application.Run(new MainForm());
            }
        }
    }
}
