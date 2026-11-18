// "Tocando agora" via controle de midia do Windows (GlobalSystemMediaTransportControls):
// funciona com Spotify, navegadores (YouTube, Deezer...), VLC, Media Player etc.
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using Windows.Media.Control;

namespace TurzxScreen
{
    static class Media
    {
        public static bool Has, Playing;
        public static string Title = "", Artist = "", Album = "", Source = "";
        public static Image Cover;
        public static TimeSpan Position, Duration;
        public static DateTime PosStamp;
        public static string Error = "";
        public static int TrackVersion; // muda a cada faixa nova
        static Thread th;
        static string lastKey = "";

        public static void Start()
        {
            if (th != null) return;
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        // posicao estimada agora (o Windows so atualiza a posicao de vez em quando)
        public static TimeSpan CurrentPosition
        {
            get
            {
                var p = Position;
                if (Playing) p += DateTime.Now - PosStamp;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p < TimeSpan.Zero ? TimeSpan.Zero : p;
            }
        }

        static void Loop()
        {
            GlobalSystemMediaTransportControlsSessionManager mgr = null;
            while (true)
            {
                try
                {
                    if (mgr == null) mgr = WinRt.Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    var s = mgr.GetCurrentSession();
                    if (s == null) { Has = false; Playing = false; }
                    else
                    {
                        var props = WinRt.Wait(s.TryGetMediaPropertiesAsync());
                        var pb = s.GetPlaybackInfo();
                        var tl = s.GetTimelineProperties();
                        Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        string title = props.Title ?? "", artist = props.Artist ?? "";
                        if (artist == "" && !string.IsNullOrEmpty(props.AlbumArtist)) artist = props.AlbumArtist;
                        Has = title != "";
                        Source = FriendlySource(s.SourceAppUserModelId);
                        Duration = tl.EndTime - tl.StartTime;
                        var newPos = tl.Position;
                        var stamp = tl.LastUpdatedTime.LocalDateTime;
                        if (stamp.Year < 2000 || stamp > DateTime.Now) stamp = DateTime.Now;
                        Position = newPos; PosStamp = stamp;
                        string key = title + "|" + artist;
                        if (key != lastKey)
                        {
                            lastKey = key;
                            Title = title; Artist = artist; Album = props.AlbumTitle ?? "";
                            Image cover = null;
                            // a capa as vezes chega um pouco depois do titulo
                            for (int i = 0; i < 3 && cover == null; i++)
                            {
                                if (i > 0) { Thread.Sleep(700); props = WinRt.Wait(s.TryGetMediaPropertiesAsync()); }
                                cover = ReadThumb(props.Thumbnail);
                            }
                            Cover = cover; // a antiga fica para o GC (a thread de render pode estar usando)
                            if (Has) TrackVersion++;
                        }
                    }
                    Error = "";
                }
                catch (Exception ex) { Error = ex.Message; Log.Error("musica", ex); mgr = null; Thread.Sleep(3000); }
                Thread.Sleep(1000);
            }
        }

        static Image ReadThumb(Windows.Storage.Streams.IRandomAccessStreamReference r)
        {
            if (r == null) return null;
            try
            {
                using (var s = WinRt.Wait(r.OpenReadAsync()))
                {
                    var img = new Bitmap(new MemoryStream(WinRt.ReadAll(s)));
                    return CropBars(img);
                }
            }
            catch { return null; }
        }

        // o Spotify/YouTube as vezes mandam a capa com faixas pretas ou transparentes; corta para quadrado central
        static Image CropBars(Bitmap b)
        {
            if (Math.Abs(b.Width - b.Height) < 4) return b;
            int s = Math.Min(b.Width, b.Height);
            var r = new Bitmap(s, s);
            using (var g = Graphics.FromImage(r))
                g.DrawImage(b, new Rectangle(0, 0, s, s), new Rectangle((b.Width - s) / 2, (b.Height - s) / 2, s, s), GraphicsUnit.Pixel);
            b.Dispose();
            return r;
        }

        static string FriendlySource(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            string a = aumid.ToLowerInvariant();
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("chrome")) return "Chrome";
            if (a.Contains("msedge") || a.Contains("edge")) return "Edge";
            if (a.Contains("firefox")) return "Firefox";
            if (a.Contains("zunemusic") || a.Contains("media")) return "Media Player";
            if (a.Contains("vlc")) return "VLC";
            if (a.Contains("deezer")) return "Deezer";
            if (a.Contains("tidal")) return "TIDAL";
            if (a.Contains("applemusic") || a.Contains("itunes")) return "Apple Music";
            string n = Path.GetFileNameWithoutExtension(aumid.Split('!')[0]);
            return n;
        }
    }
}
