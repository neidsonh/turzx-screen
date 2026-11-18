// Leitura das notificacoes do Windows (central de notificacoes) via WinRT UserNotificationListener.
// Funciona em app desktop comum; o evento NotificationChanged exige app empacotado, entao fazemos polling.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TurzxScreen
{
    class ToastInfo
    {
        public uint Id;
        public string App = "", Title = "", Body = "";
        public DateTime Time;
        public Image Icon;
        public bool IsMusic;
        public bool Direct;   // veio da API: nao passa pelos filtros de notificacao
        public int Seconds;   // 0 = tempo configurado no app
    }

    static class WinRt
    {
        // espera uma IAsyncOperation sem precisar do System.Runtime.WindowsRuntime (que exige o Windows.winmd do SDK)
        public static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op, int timeoutMs = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (op.Status == Windows.Foundation.AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) { op.Cancel(); throw new TimeoutException("WinRT timeout"); }
                Thread.Sleep(5);
            }
            if (op.Status == Windows.Foundation.AsyncStatus.Error) throw new Exception("WinRT: " + op.ErrorCode.Message);
            return op.GetResults();
        }

        public static byte[] ReadAll(Windows.Storage.Streams.IRandomAccessStream s)
        {
            uint size = (uint)s.Size;
            using (var r = new Windows.Storage.Streams.DataReader(s.GetInputStreamAt(0)))
            {
                uint got = Wait(r.LoadAsync(size));
                var buf = new byte[got];
                r.ReadBytes(buf);
                return buf;
            }
        }
    }

    static class NotificationWatcher
    {
        static readonly object sync = new object();
        static readonly Queue<ToastInfo> queue = new Queue<ToastInfo>();
        static HashSet<uint> seen;
        static Thread th;
        static volatile bool run;
        static readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();
        public static string LastError = "";
        public static bool Available;

        public static void Start()
        {
            if (th != null) return;
            run = true;
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        public static void Inject(ToastInfo t) { lock (sync) queue.Enqueue(t); }

        public static ToastInfo Dequeue()
        {
            lock (sync) return queue.Count > 0 ? queue.Dequeue() : null;
        }

        static void Loop()
        {
            UserNotificationListener l = null;
            while (run)
            {
                try
                {
                    if (l == null)
                    {
                        l = UserNotificationListener.Current;
                        var st = WinRt.Wait(l.RequestAccessAsync(), 60000);
                        if (st != UserNotificationListenerAccessStatus.Allowed)
                        {
                            LastError = "Acesso as notificacoes negado. Libere em Configuracoes > Privacidade > Notificacoes.";
                            l = null; Thread.Sleep(10000); continue;
                        }
                        Available = true;
                    }
                    var list = WinRt.Wait(l.GetNotificationsAsync(NotificationKinds.Toast));
                    var ids = new HashSet<uint>();
                    foreach (var n in list)
                    {
                        ids.Add(n.Id);
                        if (seen == null || seen.Contains(n.Id)) continue;
                        var t = Convert(n);
                        if (t != null && (DateTime.Now - t.Time).TotalMinutes < 2) lock (sync) queue.Enqueue(t);
                    }
                    seen = ids; // primeira volta: so marca as existentes como vistas
                    LastError = "";
                }
                catch (Exception ex) { LastError = ex.InnerException != null ? ex.InnerException.Message : ex.Message; l = null; }
                Thread.Sleep(1000);
            }
        }

        static ToastInfo Convert(UserNotification n)
        {
            var t = new ToastInfo();
            t.Id = n.Id;
            t.Time = n.CreationTime.LocalDateTime;
            try { t.App = n.AppInfo.DisplayInfo.DisplayName; } catch { }
            try
            {
                var b = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (b != null)
                {
                    var texts = b.GetTextElements();
                    if (texts.Count > 0) t.Title = texts[0].Text;
                    var body = new List<string>();
                    for (int i = 1; i < texts.Count; i++) if (!string.IsNullOrWhiteSpace(texts[i].Text)) body.Add(texts[i].Text.Trim());
                    t.Body = string.Join("\n", body.ToArray());
                }
            }
            catch { }
            t.Icon = GetIcon(n);
            return t;
        }

        static Image GetIcon(UserNotification n)
        {
            string key = "";
            try { key = n.AppInfo.AppUserModelId; } catch { }
            lock (iconCache) { Image c; if (iconCache.TryGetValue(key, out c)) return c; }
            Image img = null;
            try
            {
                var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                using (var ras = WinRt.Wait(logo.OpenReadAsync()))
                    img = new Bitmap(new MemoryStream(WinRt.ReadAll(ras)));
            }
            catch { }
            lock (iconCache) iconCache[key] = img;
            return img;
        }
    }
}
