// Tema Gaming: detecta o jogo em primeiro plano e mede FPS.
// Fonte principal: eventos ETW do DXGI (nao injeta nada no jogo; exige o grupo Performance Log Users ou admin).
// Reserva: memoria compartilhada do RivaTuner Statistics Server, se ele estiver aberto (Vulkan/OpenGL).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TurzxScreen
{
    static class PresentTrace
    {
        const string SessionName = "TurzxScreenFps";
        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        const int PresentStart = 42, PresentMpoStart = 55;
        const int PropsSize = 120 + 256;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventCallback(IntPtr record);
        static readonly EventCallback callback = OnEvent; // referencia mantida: o Windows chama por ponteiro

        static readonly object sync = new object();
        static readonly long[] stamps = new long[4096];
        static int head, stored;
        public static volatile int TargetPid;
        public static volatile string Error = "";
        static ulong session, consumer;
        static IntPtr nameMem = IntPtr.Zero, logMem = IntPtr.Zero;
        public static bool Running { get { return session != 0; } }

        static IntPtr NewProps()
        {
            var p = Marshal.AllocHGlobal(PropsSize);
            for (int i = 0; i < PropsSize; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, PropsSize);        // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, 0x20000);         // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);              // BufferSize (KB)
            Marshal.WriteInt32(p, 64, 0x100);           // EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);               // FlushTimer
            Marshal.WriteInt32(p, 116, 120);            // LoggerNameOffset
            return p;
        }

        public static void StopExisting()
        {
            var p = NewProps();
            try { ControlTraceW(0, SessionName, p, 1); } finally { Marshal.FreeHGlobal(p); }
        }

        public static void Start()
        {
            if (session != 0) return;
            if (IntPtr.Size != 8) { Error = "requer processo de 64 bits"; return; }
            StopExisting();
            var props = NewProps();
            try
            {
                ulong h;
                int rc = StartTraceW(out h, SessionName, props);
                if (rc != 0) { Error = rc == 5 ? "sem permissao para eventos graficos" : "ETW erro " + rc; Log.Write("fps", Error); return; }
                session = h;
                var provider = DxgiProvider;
                rc = EnableTraceEx2(h, ref provider, 1, 4, 0, 0, 0, IntPtr.Zero);
                if (rc != 0) { Error = "ETW provider erro " + rc; Log.Write("fps", Error); Stop(); return; }

                // EVENT_TRACE_LOGFILEW (x64): LoggerName @8, ProcessTraceMode @28, EventRecordCallback @424
                logMem = Marshal.AllocHGlobal(448);
                for (int i = 0; i < 448; i++) Marshal.WriteByte(logMem, i, 0);
                nameMem = Marshal.StringToHGlobalUni(SessionName);
                Marshal.WriteIntPtr(logMem, 8, nameMem);
                Marshal.WriteInt32(logMem, 28, 0x100 | 0x1000 | 0x10000000); // REAL_TIME | RAW_TIMESTAMP | EVENT_RECORD
                Marshal.WriteIntPtr(logMem, 424, Marshal.GetFunctionPointerForDelegate(callback));
                consumer = OpenTraceW(logMem);
                if (consumer == ulong.MaxValue) { consumer = 0; Error = "ETW OpenTrace erro " + Marshal.GetLastWin32Error(); Log.Write("fps", Error); Stop(); return; }
                ulong handle = consumer;
                var th = new Thread(delegate() { try { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); } catch (Exception ex) { Log.Error("fps", ex); } });
                th.IsBackground = true; th.Start();
                Error = "";
            }
            finally { Marshal.FreeHGlobal(props); }
        }

        public static void Stop()
        {
            if (session == 0) return;
            var p = NewProps();
            try { ControlTraceW(session, null, p, 1); } finally { Marshal.FreeHGlobal(p); }
            session = 0;
            if (consumer != 0) { CloseTrace(consumer); consumer = 0; }
            // nameMem/logMem ficam reservados: a thread de ProcessTrace pode ainda estar encerrando
            lock (sync) { stored = 0; head = 0; }
        }

        static void OnEvent(IntPtr r)
        {
            // EVENT_RECORD: ProcessId @12, TimeStamp @16, EventDescriptor.Id @40
            int id = (ushort)Marshal.ReadInt16(r, 40);
            if (id != PresentStart && id != PresentMpoStart) return;
            if (Marshal.ReadInt32(r, 12) != TargetPid) return;
            long ts = Marshal.ReadInt64(r, 16);
            lock (sync)
            {
                stamps[head] = ts;
                head = (head + 1) % stamps.Length;
                if (stored < stamps.Length) stored++;
            }
        }

        public static void ResetSamples() { lock (sync) { stored = 0; head = 0; } }

        public static bool Read(out double fps, out double low1, out double ms)
        {
            fps = 0; low1 = 0; ms = 0;
            long now = Stopwatch.GetTimestamp(), freq = Stopwatch.Frequency;
            long[] a;
            lock (sync)
            {
                a = new long[stored];
                for (int i = 0; i < stored; i++) a[i] = stamps[(head - stored + i + stamps.Length) % stamps.Length];
            }
            int last = 0;
            var gaps = new List<double>();
            for (int i = 0; i < a.Length; i++)
            {
                long age = now - a[i];
                if (age > freq * 15) continue;
                if (age <= freq * 2) last++;
                if (i > 0 && now - a[i - 1] <= freq * 15) gaps.Add((a[i] - a[i - 1]) * 1000.0 / freq);
            }
            if (last == 0) return false;
            fps = last / 2.0;
            ms = 1000 / fps;
            if (gaps.Count >= 20)
            {
                gaps.Sort();
                double p99 = gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.99))];
                if (p99 > 0) low1 = 1000 / p99;
            }
            return true;
        }
    }

    static class Rtss
    {
        // RTSS_SHARED_MEMORY v2: entrada: pid @0, time0 @268, time1 @272, frames @276, frametime(us) @280
        public static bool Read(int pid, out double fps, out double ms)
        {
            fps = 0; ms = 0;
            try
            {
                using (var m = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read))
                using (var v = m.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    if (v.ReadUInt32(0) != 0x52545353) return false;
                    int size = v.ReadInt32(8), off = v.ReadInt32(12), count = v.ReadInt32(16);
                    for (int i = 0; i < count && i < 256; i++)
                    {
                        long e = off + (long)i * size;
                        if (v.ReadInt32(e) != pid) continue;
                        uint t0 = v.ReadUInt32(e + 268), t1 = v.ReadUInt32(e + 272), frames = v.ReadUInt32(e + 276);
                        if (t1 <= t0 || frames == 0) return false;
                        fps = 1000.0 * frames / (t1 - t0);
                        ms = v.ReadUInt32(e + 280) / 1000.0;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }

    static class Gaming
    {
        public const int HLen = 512;
        public const double HScale = 144;
        public static volatile bool Active;
        public static volatile string Name = "";
        public static volatile string Source = "";
        public static double Fps = -1, Low1 = -1, FrameMs = -1;
        public static float[] Hist = new float[HLen];
        public static int HCount;
        public static int ForcePid;
        static Thread th;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder s, ref int n);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "vlc", "mpv", "potplayer", "potplayermini64",
            "wmplayer", "applicationframehost", "turzxscreen", "lockapp", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
            "ms-teams", "teams", "discord", "spotify", "code", "devenv", "claude", "powerpnt", "mstsc", "obs64", "steamwebhelper", "steam",
            "epicgameslauncher", "nvidia overlay", "nvidia app", "windowsterminal", "taskmgr" };
        static readonly string[] GameDirs = { @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\xboxgames\", @"\riot games\", @"\battle.net\games\", @"\ubisoft game launcher\games\" };

        public static void Start()
        {
            if (th != null) return;
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        static string ImagePath(int pid)
        {
            var h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        static bool Alive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch { return false; }
        }

        static void Loop()
        {
            int pid = 0, smooth = 0; DateTime lastSeen = DateTime.MinValue, lastCandidate = DateTime.MinValue;
            try { PresentTrace.StopExisting(); } catch { } // sobra de uma execucao interrompida
            while (true)
            {
                try
                {
                    int fgPid = 0; bool known = false, full = false; string title = "", exe = "";
                    var h = GetForegroundWindow();
                    if (ForcePid != 0) { fgPid = ForcePid; known = true; title = "Teste de FPS"; }
                    else if (h != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(h, out fgPid);
                        string path = ImagePath(fgPid);
                        exe = Path.GetFileNameWithoutExtension(path);
                        if (fgPid == Process.GetCurrentProcess().Id || exe == "" || NotGames.Contains(exe)) fgPid = 0;
                        else
                        {
                            string low = path.ToLowerInvariant();
                            foreach (var d in GameDirs) if (low.Contains(d)) known = true;
                            RECT r;
                            if (GetWindowRect(h, out r))
                            {
                                var b = Screen.FromHandle(h).Bounds;
                                full = r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom;
                            }
                            var sb = new StringBuilder(128); GetWindowTextW(h, sb, sb.Capacity); title = sb.ToString().Trim();
                            if (title == "") title = exe;
                        }
                    }

                    bool candidate = fgPid != 0 && (known || full);
                    if (candidate)
                    {
                        lastCandidate = DateTime.Now;
                        if (fgPid != pid)
                        {
                            pid = fgPid; smooth = 0; PresentTrace.ResetSamples(); PresentTrace.TargetPid = pid;
                            Active = false; Fps = Low1 = FrameMs = -1;
                        }
                        Name = title; lastSeen = DateTime.Now;
                        if (!PresentTrace.Running) PresentTrace.Start();
                    }

                    if (pid != 0)
                    {
                        double fps, low, ms; string src = "";
                        bool ok = PresentTrace.Read(out fps, out low, out ms);
                        if (ok) src = "Windows";
                        else if (Rtss.Read(pid, out fps, out ms)) { ok = true; low = -1; src = "RivaTuner"; }
                        Fps = ok ? fps : -1; Low1 = ok && low > 0 ? low : -1; FrameMs = ok ? ms : -1; Source = src;
                        smooth = ok && fps >= 15 ? Math.Min(smooth + 1, 5) : Math.Max(smooth - 1, 0);
                        if (known || smooth >= 3) Active = true;
                        if (Active && ok) { Hist[HCount % HLen] = (float)Math.Min(100, fps * 100 / HScale); HCount++; }
                        if ((DateTime.Now - lastSeen).TotalSeconds > 8 || !Alive(pid))
                        {
                            pid = 0; PresentTrace.TargetPid = 0; Active = false; Name = ""; Fps = Low1 = FrameMs = -1; Source = "";
                        }
                    }
                    if (pid == 0 && PresentTrace.Running && (DateTime.Now - lastCandidate).TotalSeconds > 20) PresentTrace.Stop();
                }
                catch (Exception ex) { Log.Error("gaming", ex); }
                Thread.Sleep(1000);
            }
        }
    }
}
