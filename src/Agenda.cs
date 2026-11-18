// Proxima reuniao: le calendarios ICS (link publicado do Outlook/Google ou arquivo .ics).
// O Outlook novo nao expoe a agenda para outros programas; o link ICS funciona com ele.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TurzxScreen
{
    class Meeting
    {
        public string Title = "", Place = "";
        public DateTime Start, End;
        public string Key { get { return Title + "|" + Start.Ticks; } }
    }

    static class Agenda
    {
        public static volatile string Sources = "";
        public static volatile List<Meeting> Items = new List<Meeting>();
        public static volatile string Error = "";
        public static DateTime LastSync = DateTime.MinValue;
        static DateTime next = DateTime.MinValue;
        static string lastSources = "";
        static Thread th;
        const int MaxBytes = 20 * 1024 * 1024;

        public static void Start()
        {
            if (th != null) return;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        public static void RefreshNow() { next = DateTime.MinValue; }

        public static Meeting Next()
        {
            var now = DateTime.Now;
            foreach (var m in Items) if (m.End > now) return m;
            return null;
        }

        static void Loop()
        {
            while (true)
            {
                try
                {
                    string src = (Sources ?? "").Trim();
                    if (src != lastSources) next = DateTime.MinValue;
                    if (src == "") { Items = new List<Meeting>(); Error = ""; lastSources = ""; }
                    else if (DateTime.Now >= next)
                    {
                        lastSources = src;
                        next = DateTime.Now.AddMinutes(5);
                        var all = new List<Meeting>(); string err = "";
                        foreach (var s in src.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            try { all.AddRange(Parse(Fetch(s.Trim()), DateTime.Now.AddDays(-1), DateTime.Now.AddDays(14))); }
                            catch (Exception ex) { err = "Nao consegui ler um calendario: " + ex.Message; Log.Write("agenda", ex.GetType().Name); }
                        }
                        // mantem a ultima lista boa se todos falharam
                        if (all.Count > 0 || err == "") { Items = Clean(all); LastSync = DateTime.Now; }
                        Error = err;
                    }
                }
                catch (Exception ex) { Error = ex.Message; Log.Error("agenda", ex); }
                Thread.Sleep(1000);
            }
        }

        static List<Meeting> Clean(List<Meeting> all)
        {
            all.Sort((a, b) => a.Start.CompareTo(b.Start));
            var seen = new HashSet<string>(); var r = new List<Meeting>();
            foreach (var m in all) if (seen.Add(m.Key)) r.Add(m);
            return r;
        }

        static string Fetch(string src)
        {
            if (src.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) src = "https://" + src.Substring(9);
            if (!src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllText(src, Encoding.UTF8);
            var req = (HttpWebRequest)WebRequest.Create(src);
            req.UserAgent = "TurzxScreen/1.0"; req.Timeout = 20000; req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var b = new byte[65536]; int n;
                while ((n = s.Read(b, 0, b.Length)) > 0)
                {
                    ms.Write(b, 0, n);
                    if (ms.Length > MaxBytes) throw new InvalidDataException("calendario grande demais");
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // ---------------------------------------------------------- ICS
        class Prop { public string Name = "", Value = ""; public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        class Vev { public Dictionary<string, List<Prop>> P = new Dictionary<string, List<Prop>>(StringComparer.OrdinalIgnoreCase);
            public Prop One(string n) { List<Prop> l; return P.TryGetValue(n, out l) ? l[0] : null; }
            public string Val(string n) { var p = One(n); return p != null ? p.Value : ""; }
            public List<Prop> All(string n) { List<Prop> l; return P.TryGetValue(n, out l) ? l : new List<Prop>(); } }
        struct When { public DateTime Wall; public TimeZoneInfo Zone; public bool AllDay; }

        static Prop ParseLine(string line)
        {
            bool quote = false; int colon = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quote = !quote;
                else if (line[i] == ':' && !quote) { colon = i; break; }
            }
            if (colon <= 0) return null;
            var p = new Prop { Value = line.Substring(colon + 1) };
            var head = line.Substring(0, colon).Split(';');
            p.Name = head[0].Trim();
            for (int i = 1; i < head.Length; i++)
            {
                int e = head[i].IndexOf('=');
                if (e > 0) p.Params[head[i].Substring(0, e)] = head[i].Substring(e + 1).Trim('"');
            }
            return p;
        }

        static readonly Dictionary<string, string> Iana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "America/Sao_Paulo", "E. South America Standard Time" }, { "America/Bahia", "Bahia Standard Time" },
            { "America/Fortaleza", "SA Eastern Standard Time" }, { "America/Recife", "SA Eastern Standard Time" }, { "America/Belem", "SA Eastern Standard Time" },
            { "America/Manaus", "SA Western Standard Time" }, { "America/Cuiaba", "Central Brazilian Standard Time" }, { "America/Campo_Grande", "Central Brazilian Standard Time" },
            { "America/Rio_Branco", "SA Pacific Standard Time" }, { "America/Noronha", "UTC-02" }, { "America/New_York", "Eastern Standard Time" },
            { "America/Chicago", "Central Standard Time" }, { "America/Denver", "Mountain Standard Time" }, { "America/Los_Angeles", "Pacific Standard Time" },
            { "Europe/London", "GMT Standard Time" }, { "Europe/Lisbon", "GMT Standard Time" }, { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Madrid", "Romance Standard Time" }, { "Europe/Berlin", "W. Europe Standard Time" }, { "Etc/UTC", "UTC" }, { "UTC", "UTC" } };

        static TimeZoneInfo Zone(string id)
        {
            if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Local;
            string win;
            if (Iana.TryGetValue(id, out win)) id = win;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Local; }
        }

        static bool ParseWhen(string v, string tzid, bool dateParam, out When w)
        {
            w = new When(); v = v.Trim();
            if (v.Length == 8 || dateParam)
            {
                w.AllDay = true; w.Zone = TimeZoneInfo.Local;
                return DateTime.TryParseExact(v.Substring(0, Math.Min(8, v.Length)), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out w.Wall);
            }
            bool utc = v.EndsWith("Z");
            w.Zone = utc ? TimeZoneInfo.Utc : Zone(tzid);
            return DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out w.Wall);
        }

        static bool ParseWhen(Prop p, out When w)
        {
            w = new When();
            if (p == null) return false;
            string tz, val;
            p.Params.TryGetValue("TZID", out tz);
            p.Params.TryGetValue("VALUE", out val);
            return ParseWhen(p.Value, tz, val == "DATE", out w);
        }

        static DateTime ToLocal(DateTime wall, TimeZoneInfo z)
        {
            var u = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (z.IsInvalidTime(u)) u = u.AddHours(1); // horario inexistente na troca de horario de verao
            return TimeZoneInfo.ConvertTime(u, z, TimeZoneInfo.Local);
        }

        static string Unescape(string s)
        {
            return s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
        }

        static TimeSpan ParseDuration(string d)
        {
            var m = Regex.Match(d ?? "", @"^[+]?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
            if (!m.Success) return TimeSpan.Zero;
            Func<int, int> g = i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
            return new TimeSpan(g(1) * 7 + g(2), g(3), g(4), g(5));
        }

        public static List<Meeting> Parse(string ics, DateTime from, DateTime to)
        {
            var text = Regex.Replace(ics, @"\r?\n[ \t]", "");
            var events = new List<Vev>(); Vev cur = null; int nested = 0;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new Vev(); nested = 0; continue; }
                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur != null) events.Add(cur); cur = null; continue; }
                if (cur == null) continue;
                if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { nested++; continue; }
                if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { nested--; continue; }
                if (nested > 0) continue;
                var p = ParseLine(line);
                if (p == null) continue;
                List<Prop> l;
                if (!cur.P.TryGetValue(p.Name, out l)) cur.P[p.Name] = l = new List<Prop>();
                l.Add(p);
            }

            // instancias alteradas: a serie nao gera essa ocorrencia
            var overridden = new HashSet<string>();
            foreach (var e in events)
            {
                When r;
                if (ParseWhen(e.One("RECURRENCE-ID"), out r) && !r.AllDay) overridden.Add(e.Val("UID") + "|" + ToLocal(r.Wall, r.Zone).Ticks);
            }

            var res = new List<Meeting>();
            foreach (var e in events)
            {
                try { AddEvent(e, overridden, from, to, res); }
                catch (Exception ex) { Log.Write("agenda", "evento ignorado: " + ex.GetType().Name); }
            }
            res.Sort((a, b) => a.Start.CompareTo(b.Start));
            return res;
        }

        static void AddEvent(Vev e, HashSet<string> overridden, DateTime from, DateTime to, List<Meeting> res)
        {
            string title = Unescape(e.Val("SUMMARY"));
            if (e.Val("STATUS").Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
            if (Regex.IsMatch(title, @"^(cancelad[oa]|canceled|cancelled)\s*:", RegexOptions.IgnoreCase)) return;
            When st, en;
            if (!ParseWhen(e.One("DTSTART"), out st) || st.AllDay) return; // dia inteiro nao e reuniao
            TimeSpan dur = ParseWhen(e.One("DTEND"), out en) && !en.AllDay
                ? ToLocal(en.Wall, en.Zone) - ToLocal(st.Wall, st.Zone)
                : ParseDuration(e.Val("DURATION"));
            if (dur <= TimeSpan.Zero) dur = TimeSpan.FromMinutes(30);

            string uid = e.Val("UID"), place = Unescape(e.Val("LOCATION")), all = place + " " + e.Val("DESCRIPTION") + " " + e.Val("X-MICROSOFT-SKYPETEAMSMEETINGURL");
            if (all.IndexOf("teams.microsoft.com", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("teams.live.com", StringComparison.OrdinalIgnoreCase) >= 0) place = "Teams";
            else if (all.IndexOf("meet.google.com", StringComparison.OrdinalIgnoreCase) >= 0) place = "Google Meet";
            else if (all.IndexOf("zoom.us", StringComparison.OrdinalIgnoreCase) >= 0) place = "Zoom";

            var skip = new HashSet<long>();
            foreach (var x in e.All("EXDATE"))
            {
                string tz, val; x.Params.TryGetValue("TZID", out tz); x.Params.TryGetValue("VALUE", out val);
                foreach (var v in x.Value.Split(','))
                {
                    When w;
                    if (ParseWhen(v, tz, val == "DATE", out w)) skip.Add(w.AllDay ? ToLocal(w.Wall.Date + st.Wall.TimeOfDay, st.Zone).Ticks : ToLocal(w.Wall, w.Zone).Ticks);
                }
            }

            var walls = new List<DateTime>();
            string rule = e.Val("RRULE");
            bool isOverride = e.One("RECURRENCE-ID") != null;
            if (rule == "" || isOverride) walls.Add(st.Wall); else Expand(rule, st, to.AddDays(2), walls);
            foreach (var w in walls)
            {
                var s = ToLocal(w, st.Zone);
                if (skip.Contains(s.Ticks)) continue;
                if (!isOverride && rule != "" && overridden.Contains(uid + "|" + s.Ticks)) continue;
                var f = s + dur;
                if (f <= from || s >= to) continue;
                res.Add(new Meeting { Title = title != "" ? title : "Reuniao", Place = place, Start = s, End = f });
            }
        }

        // ---------------------------------------------------------- RRULE
        struct ByDay { public int Ord; public DayOfWeek Day; }
        static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        static List<int> Ints(Dictionary<string, string> r, string k)
        {
            var l = new List<int>(); string v;
            if (r.TryGetValue(k, out v)) foreach (var s in v.Split(',')) { int n; if (int.TryParse(s, out n)) l.Add(n); }
            return l;
        }

        static List<DateTime> MonthDays(int y, int mo, List<ByDay> days, List<int> monthDays, List<int> setPos, int startDay)
        {
            int dim = DateTime.DaysInMonth(y, mo);
            var d = new List<DateTime>();
            if (monthDays.Count > 0)
            {
                foreach (var n in monthDays) { int x = n > 0 ? n : dim + n + 1; if (x >= 1 && x <= dim) d.Add(new DateTime(y, mo, x)); }
            }
            else if (days.Count > 0)
            {
                foreach (var b in days)
                {
                    var match = new List<DateTime>();
                    for (int i = 1; i <= dim; i++) { var t = new DateTime(y, mo, i); if (t.DayOfWeek == b.Day) match.Add(t); }
                    if (b.Ord == 0) d.AddRange(match);
                    else { int idx = b.Ord > 0 ? b.Ord - 1 : match.Count + b.Ord; if (idx >= 0 && idx < match.Count) d.Add(match[idx]); }
                }
            }
            else if (startDay <= dim) d.Add(new DateTime(y, mo, startDay));
            d.Sort();
            if (setPos.Count == 0) return d;
            var r = new List<DateTime>();
            foreach (var p in setPos) { int i = p > 0 ? p - 1 : d.Count + p; if (i >= 0 && i < d.Count) r.Add(d[i]); }
            r.Sort();
            return r;
        }

        static void Expand(string rule, When st, DateTime limitLocal, List<DateTime> output)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in rule.Split(';')) { int e = part.IndexOf('='); if (e > 0) r[part.Substring(0, e)] = part.Substring(e + 1); }
            string freq; if (!r.TryGetValue("FREQ", out freq)) return;
            freq = freq.ToUpperInvariant();
            var iv = Ints(r, "INTERVAL"); int interval = iv.Count > 0 && iv[0] > 0 ? iv[0] : 1;
            var cnt = Ints(r, "COUNT"); int maxCount = cnt.Count > 0 ? cnt[0] : int.MaxValue;
            DateTime until = DateTime.MaxValue; string us;
            if (r.TryGetValue("UNTIL", out us))
            {
                When u;
                if (ParseWhen(us, null, false, out u))
                    until = u.AllDay ? u.Wall.Date.AddDays(1).AddTicks(-1) : TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(u.Wall, DateTimeKind.Unspecified), u.Zone, st.Zone);
            }
            var days = new List<ByDay>(); string bd;
            if (r.TryGetValue("BYDAY", out bd))
                foreach (var s in bd.Split(','))
                {
                    var m = Regex.Match(s.Trim().ToUpperInvariant(), @"^([+-]?\d+)?(SU|MO|TU|WE|TH|FR|SA)$");
                    if (m.Success) days.Add(new ByDay { Ord = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0, Day = (DayOfWeek)Array.IndexOf(DayCodes, m.Groups[2].Value) });
                }
            var monthDays = Ints(r, "BYMONTHDAY"); var months = Ints(r, "BYMONTH"); var setPos = Ints(r, "BYSETPOS");
            DateTime start = st.Wall, d0 = start.Date; var tod = start.TimeOfDay;
            DateTime limit = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(limitLocal, DateTimeKind.Unspecified), TimeZoneInfo.Local, st.Zone);
            int count = 0;

            for (int k = 0; k < 20000; k++)
            {
                var dates = new List<DateTime>();
                if (freq == "DAILY")
                {
                    var d = d0.AddDays((long)k * interval);
                    bool ok = days.Count == 0;
                    foreach (var b in days) if (b.Day == d.DayOfWeek) ok = true;
                    if (ok) dates.Add(d);
                    if (d > limit) return;
                }
                else if (freq == "WEEKLY")
                {
                    var monday = d0.AddDays(-(((int)d0.DayOfWeek + 6) % 7)).AddDays(7L * k * interval);
                    if (monday > limit) return;
                    if (days.Count == 0) dates.Add(monday.AddDays(((int)d0.DayOfWeek + 6) % 7));
                    else foreach (var b in days) dates.Add(monday.AddDays(((int)b.Day + 6) % 7));
                    dates.Sort();
                }
                else if (freq == "MONTHLY")
                {
                    var first = new DateTime(d0.Year, d0.Month, 1).AddMonths(k * interval);
                    if (first > limit) return;
                    dates = MonthDays(first.Year, first.Month, days, monthDays, setPos, d0.Day);
                }
                else if (freq == "YEARLY")
                {
                    int y = d0.Year + k * interval;
                    if (y > limit.Year) return;
                    foreach (var mo in months.Count > 0 ? months : new List<int> { d0.Month })
                        if (mo >= 1 && mo <= 12) dates.AddRange(MonthDays(y, mo, days, monthDays, setPos, d0.Day));
                    dates.Sort();
                }
                else return;

                foreach (var d in dates)
                {
                    var w = d + tod;
                    if (w < start) continue;
                    if (w > until || w > limit) return;
                    if (++count > maxCount) return;
                    output.Add(w);
                }
            }
        }

        // uso de desenvolvimento: TurzxScreen.exe /icstest entrada.ics saida.txt
        public static void Dump(string input, string output)
        {
            var sb = new StringBuilder();
            foreach (var m in Parse(File.ReadAllText(input, Encoding.UTF8), DateTime.Now.AddDays(-1), DateTime.Now.AddDays(14)))
                sb.AppendLine(m.Start.ToString("yyyy-MM-dd HH:mm") + " -> " + m.End.ToString("HH:mm") + " | " + m.Title + " | " + m.Place);
            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
        }
    }
}
