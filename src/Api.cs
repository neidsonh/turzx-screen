// API local (somente 127.0.0.1) para mandar avisos, status e imagens para a tela,
// e o modo "/claude-hook", chamado pelos hooks do Claude Code.
// Protecoes: escuta so no loopback, exige token (api-token.txt), confere o Host e recusa
// requisicoes com Origin (paginas da web nao conseguem usar a API).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TurzxScreen
{
    // ------------------------------------------------------------------ Quadro de status
    class StatusItem
    {
        public string Key = "", Label = "", State = "info", Text = "", Detail = "";
        public DateTime Updated, Expires;
        public int Priority
        {
            get { return State == "waiting" ? 4 : State == "error" ? 3 : State == "working" ? 2 : State == "done" ? 1 : 0; }
        }
    }

    static class StatusBoard
    {
        static readonly object sync = new object();
        static readonly Dictionary<string, StatusItem> items = new Dictionary<string, StatusItem>();
        static readonly Dictionary<string, DateTime> removed = new Dictionary<string, DateTime>();
        public static volatile bool Show = true;

        // soft: evento de ferramenta. Hooks assincronos podem chegar fora de ordem, entao
        // um evento soft nao desfaz um "terminou"/"precisa de voce" recem-recebido.
        public static void Set(StatusItem n, bool soft)
        {
            lock (sync)
            {
                var now = DateTime.Now;
                if (soft)
                {
                    DateTime gone; StatusItem cur;
                    if (removed.TryGetValue(n.Key, out gone) && (now - gone).TotalSeconds < 5) return;
                    if (items.TryGetValue(n.Key, out cur) && cur.State != "working" && (now - cur.Updated).TotalSeconds < 2) return;
                }
                n.Updated = now;
                items[n.Key] = n;
                removed.Remove(n.Key);
            }
        }

        public static void Remove(string key)
        {
            lock (sync) { items.Remove(key); removed[key] = DateTime.Now; }
        }

        public static StatusItem Top(out int total)
        {
            lock (sync)
            {
                var now = DateTime.Now;
                var dead = new List<string>();
                StatusItem best = null;
                foreach (var kv in items)
                {
                    var i = kv.Value;
                    if (i.Expires <= now) { dead.Add(kv.Key); continue; }
                    if (best == null || i.Priority > best.Priority || (i.Priority == best.Priority && i.Updated > best.Updated)) best = i;
                }
                foreach (var k in dead) items.Remove(k);
                total = items.Count;
                return best;
            }
        }
    }

    // ------------------------------------------------------------------ Servidor
    static class ApiServer
    {
        public const int MaxBody = 8 * 1024 * 1024;
        public static string Token = "";
        public static int Port = 48765;
        public static string LastError = "";
        public static Func<Dictionary<string, object>> GetStatus;
        public static Bitmap Overlay;
        public static DateTime OverlayUntil = DateTime.MinValue;
        static TcpListener listener;
        static readonly object sync = new object();

        public static bool Running { get { return listener != null; } }
        static string TokenPath { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "api-token.txt"); } }

        public static string LoadToken(bool create)
        {
            try
            {
                if (File.Exists(TokenPath))
                {
                    string t = File.ReadAllText(TokenPath).Trim();
                    if (t.Length >= 32) return t;
                }
                if (!create) return "";
                var b = new byte[32];
                using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
                string s = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(TokenPath, s);
                return s;
            }
            catch (Exception ex) { Log.Error("api", ex); return ""; }
        }

        public static void Start()
        {
            lock (sync)
            {
                if (listener != null) return;
                try
                {
                    Token = LoadToken(true);
                    if (Token == "") throw new InvalidOperationException("token indisponivel");
                    var l = new TcpListener(IPAddress.Loopback, Port);
                    l.Start();
                    listener = l; LastError = "";
                    var th = new Thread(delegate() { AcceptLoop(l); }); th.IsBackground = true; th.Start();
                }
                catch (Exception ex) { listener = null; LastError = ex.Message; Log.Error("api", ex); }
            }
        }

        public static void Stop()
        {
            lock (sync)
            {
                if (listener == null) return;
                try { listener.Stop(); } catch { }
                listener = null;
            }
        }

        static void AcceptLoop(TcpListener l)
        {
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { return; } // listener parado
                ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
            }
        }

        static int FindHeaderEnd(byte[] b, int n)
        {
            for (int i = 3; i < n; i++) if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i + 1;
            return -1;
        }

        static void Handle(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 3000; c.SendTimeout = 3000;
                    var s = c.GetStream();
                    var buf = new byte[16384]; int got = 0, end = -1;
                    while (end < 0)
                    {
                        if (got == buf.Length) { Reply(s, 431, Err("cabecalho grande demais")); return; }
                        int r = s.Read(buf, got, buf.Length - got);
                        if (r <= 0) return;
                        got += r; end = FindHeaderEnd(buf, got);
                    }
                    var lines = Encoding.ASCII.GetString(buf, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    var first = lines[0].Split(' ');
                    if (first.Length < 2) { Reply(s, 400, Err("requisicao invalida")); return; }
                    var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int p = lines[i].IndexOf(':');
                        if (p > 0) h[lines[i].Substring(0, p).Trim()] = lines[i].Substring(p + 1).Trim();
                    }

                    string host, token, origin, lenText;
                    h.TryGetValue("Host", out host); h.TryGetValue("X-Turzx-Token", out token); h.TryGetValue("Origin", out origin);
                    if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) { Reply(s, 403, Err("host nao permitido")); return; }
                    if (origin != null) { Reply(s, 403, Err("chamadas de paginas web nao sao permitidas")); return; }
                    if (!SameToken(token, Token)) { Reply(s, 401, Err("token invalido")); return; }

                    int len = 0;
                    if (h.TryGetValue("Content-Length", out lenText) && (!int.TryParse(lenText, out len) || len < 0)) { Reply(s, 400, Err("Content-Length invalido")); return; }
                    if (len > MaxBody) { Reply(s, 413, Err("corpo grande demais (limite 8 MB)")); return; }
                    var body = new byte[len];
                    int have = Math.Min(len, got - end);
                    Buffer.BlockCopy(buf, end, body, 0, have);
                    while (have < len)
                    {
                        int r = s.Read(body, have, len - have);
                        if (r <= 0) { Reply(s, 400, Err("corpo incompleto")); return; }
                        have += r;
                    }
                    string path = first[1], query = "";
                    int q = path.IndexOf('?');
                    if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
                    Route(s, first[0], path, query, body);
                }
                catch (Exception ex) { Log.Error("api", ex); }
            }
        }

        static bool SameToken(string a, string b)
        {
            if (a == null || b == "" || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static void Route(Stream s, string method, string path, string query, byte[] body)
        {
            var js = new JavaScriptSerializer();
            if (method == "GET" && path == "/status")
            {
                var d = GetStatus != null ? GetStatus() : new Dictionary<string, object>();
                d["ok"] = true;
                Reply(s, 200, js.Serialize(d));
                return;
            }
            if (method != "POST") { Reply(s, 404, Err("rota desconhecida")); return; }
            if (path == "/image") { PostImage(s, query, body); return; }

            Dictionary<string, object> j;
            try { j = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); }
            catch { Reply(s, 400, Err("JSON invalido")); return; }
            if (j == null) { Reply(s, 400, Err("JSON invalido")); return; }

            if (path == "/notify")
            {
                var t = new ToastInfo();
                t.App = Text(j, "app", 40); if (t.App == "") t.App = "API local";
                t.Title = Text(j, "title", 120); t.Body = Text(j, "body", 500);
                if (t.Title == "" && t.Body == "") { Reply(s, 400, Err("informe title ou body")); return; }
                t.Time = DateTime.Now; t.Direct = true; t.Seconds = Number(j, "seconds", 0, 0, 120);
                NotificationWatcher.Inject(t);
                Reply(s, 200, "{\"ok\":true}");
            }
            else if (path == "/state")
            {
                string key = Text(j, "key", 100);
                if (key == "") { Reply(s, 400, Err("informe key")); return; }
                var i = new StatusItem();
                i.Key = key; i.Label = Text(j, "label", 40); i.Text = Text(j, "text", 80); i.Detail = Text(j, "detail", 60);
                string st = Text(j, "state", 12);
                i.State = st == "working" || st == "waiting" || st == "done" || st == "error" ? st : "info";
                i.Expires = DateTime.Now.AddSeconds(Number(j, "ttl", 600, 1, 86400));
                object soft;
                StatusBoard.Set(i, j.TryGetValue("soft", out soft) && soft is bool && (bool)soft);
                Reply(s, 200, "{\"ok\":true}");
            }
            else if (path == "/clear")
            {
                string key = Text(j, "key", 100);
                if (key == "") { Reply(s, 400, Err("informe key")); return; }
                StatusBoard.Remove(key);
                Reply(s, 200, "{\"ok\":true}");
            }
            else Reply(s, 404, Err("rota desconhecida"));
        }

        static void PostImage(Stream s, string query, byte[] body)
        {
            int seconds = 10;
            var m = Regex.Match(query, @"(?:^|&)seconds=(\d{1,3})(?:&|$)");
            if (m.Success) seconds = Math.Max(1, Math.Min(300, int.Parse(m.Groups[1].Value)));
            try
            {
                using (var src = Image.FromStream(new MemoryStream(body)))
                {
                    if (src.Width > 4096 || src.Height > 4096) { Reply(s, 413, Err("imagem acima de 4096 px")); return; }
                    Overlay = new Bitmap(src); // a anterior fica para o GC: a thread de render pode estar usando
                    OverlayUntil = DateTime.Now.AddSeconds(seconds);
                }
                Reply(s, 200, "{\"ok\":true}");
            }
            catch (ArgumentException) { Reply(s, 400, Err("imagem invalida")); }
        }

        static string Text(Dictionary<string, object> j, string k, int max)
        {
            object o;
            if (!j.TryGetValue(k, out o) || o == null) return "";
            var sb = new StringBuilder();
            foreach (char c in Convert.ToString(o))
                if (c == '\n' || !char.IsControl(c)) { sb.Append(c); if (sb.Length >= max) break; }
            return sb.ToString().Trim();
        }

        static int Number(Dictionary<string, object> j, string k, int def, int min, int max)
        {
            object o; double d;
            if (!j.TryGetValue(k, out o) || o == null) return def;
            if (!double.TryParse(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return def;
            return (int)Math.Max(min, Math.Min(max, d));
        }

        static string Err(string m) { return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "ok", false }, { "error", m } }); }

        static void Reply(Stream s, int code, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            string reason = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 413 ? "Payload Too Large" : "Error";
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
        }
    }

    // ------------------------------------------------------------------ Hook do Claude Code
    // TurzxScreen.exe /claude-hook: le o evento no stdin e avisa o app. Nunca falha nem imprime nada,
    // para nao interferir no Claude Code. Envia so estado, ferramenta e nome da pasta (nunca prompts).
    static class ClaudeHook
    {
        public static void Run()
        {
            try
            {
                string input = ReadInput();
                string ev = Field(input, "hook_event_name"), session = Field(input, "session_id");
                if (ev == "" || session == "") return;
                string token = ApiServer.LoadToken(false);
                if (token == "") return;
                int port = Settings.Load().GetInt("apiport", 48765);
                string key = "claude:" + session;
                if (ev == "SessionEnd") { Post(port, token, "/clear", new Dictionary<string, object> { { "key", key } }); return; }

                string state, text; int ttl = 1800; bool soft = false;
                switch (ev)
                {
                    case "UserPromptSubmit": state = "working"; text = "trabalhando"; break;
                    case "PreToolUse": state = "working"; text = ToolText(Field(input, "tool_name")); soft = true; break;
                    case "PostToolUse": state = "working"; text = "trabalhando"; soft = true; break;
                    case "PermissionRequest": state = "waiting"; text = "pedindo permissao"; ttl = 3600; break;
                    case "Stop": state = "done"; text = "terminou"; ttl = 180; break;
                    case "StopFailure": state = "error"; text = "parou com erro"; ttl = 900; break;
                    case "Notification":
                        string type = Field(input, "notification_type");
                        if (type == "idle_prompt") { state = "waiting"; text = "aguardando voce"; ttl = 900; }
                        else if (type == "permission_prompt" || type == "elicitation_dialog") { state = "waiting"; text = "precisa de voce"; ttl = 3600; }
                        else return;
                        break;
                    default: return;
                }
                string cwd = Field(input, "cwd"), folder = "";
                try { folder = Path.GetFileName(cwd.TrimEnd('\\', '/')); } catch { }
                Post(port, token, "/state", new Dictionary<string, object> {
                    { "key", key }, { "label", "Claude Code" }, { "state", state }, { "text", text },
                    { "detail", folder }, { "ttl", ttl }, { "soft", soft } });
            }
            catch { }
        }

        static string ToolText(string t)
        {
            if (t == "Bash" || t == "PowerShell") return "rodando comando";
            if (t == "Edit" || t == "Write" || t == "NotebookEdit") return "editando arquivo";
            if (t == "Read" || t == "Grep" || t == "Glob") return "lendo arquivos";
            if (t == "WebFetch" || t == "WebSearch") return "pesquisando";
            if (t == "Agent" || t == "Task") return "com subagente";
            return "trabalhando";
        }

        // guarda so o inicio (os campos usados vem antes do conteudo da ferramenta) e drena o resto
        static string ReadInput()
        {
            var kept = new MemoryStream();
            var th = new Thread(delegate()
            {
                try
                {
                    using (var s = Console.OpenStandardInput())
                    {
                        var b = new byte[16384]; int n;
                        while ((n = s.Read(b, 0, b.Length)) > 0)
                            lock (kept) { if (kept.Length < 65536) kept.Write(b, 0, n); }
                    }
                }
                catch { }
            });
            th.IsBackground = true; th.Start(); th.Join(1500);
            lock (kept) return Encoding.UTF8.GetString(kept.ToArray());
        }

        static string Field(string json, string name)
        {
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
            if (!m.Success) return "";
            try { return new JavaScriptSerializer().Deserialize<string>(m.Groups[1].Value) ?? ""; }
            catch { return ""; }
        }

        static void Post(int port, string token, string path, Dictionary<string, object> data)
        {
            var body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data));
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Method = "POST"; req.ContentType = "application/json"; req.Proxy = null;
            req.Timeout = 800; req.ReadWriteTimeout = 800; req.KeepAlive = false;
            req.Headers["X-Turzx-Token"] = token;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (req.GetResponse()) { }
        }
    }
}
