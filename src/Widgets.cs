// Clima (Open-Meteo) e cotacoes (AwesomeAPI) - APIs gratuitas, sem chave.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TurzxScreen
{
    class HourFc { public DateTime Time; public double Temp; public int Code; }
    class Quote { public string Code = "", In = ""; public double Bid, Pct; }

    static class Widgets
    {
        // configuracao (escrita pela UI)
        public static volatile string City = "";
        public static volatile string QuoteList = "USD-BRL,EUR-BRL,BTC-BRL";

        // clima
        public static bool HasWeather;
        public static string Place = "", WeatherError = "";
        public static double Temp, Feels, Humidity, TMax, TMin;
        public static int Code; public static bool IsDay = true;
        public static List<HourFc> Hours = new List<HourFc>();
        // cotacoes
        public static List<Quote> Quotes = new List<Quote>();
        public static string QuoteError = "";

        static Thread th;
        static string geoCity; static double lat, lon;
        static DateTime nextWeather = DateTime.MinValue, nextQuotes = DateTime.MinValue;
        static string lastQuoteList = "";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static void Start()
        {
            if (th != null) return;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        public static void RefreshNow() { nextWeather = DateTime.MinValue; nextQuotes = DateTime.MinValue; }

        static string Get(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "TurzxScreen/1.0";
                return wc.DownloadString(url);
            }
        }

        static double D(object o) { return Convert.ToDouble(o, IC); }

        static void Loop()
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            while (true)
            {
                try
                {
                    string city = (City ?? "").Trim();
                    if (city != geoCity) { nextWeather = DateTime.MinValue; }
                    if (city != "" && DateTime.Now >= nextWeather)
                    {
                        nextWeather = DateTime.Now.AddMinutes(10);
                        if (city != geoCity)
                        {
                            var geo = js.Deserialize<Dictionary<string, object>>(Get(
                                "https://geocoding-api.open-meteo.com/v1/search?count=1&language=pt&name=" + Uri.EscapeDataString(city)));
                            object res;
                            if (!geo.TryGetValue("results", out res) || ((ArrayList)res).Count == 0)
                            { WeatherError = "Cidade nao encontrada: " + city; HasWeather = false; geoCity = city; lat = double.NaN; continue; }
                            var r = (Dictionary<string, object>)((ArrayList)res)[0];
                            lat = D(r["latitude"]); lon = D(r["longitude"]);
                            Place = Convert.ToString(r["name"]);
                            geoCity = city;
                        }
                        if (!double.IsNaN(lat)) FetchWeather(js);
                    }
                    if (city == "") { HasWeather = false; WeatherError = ""; geoCity = ""; }
                }
                catch (Exception ex) { WeatherError = "Clima: " + ex.Message; Log.Error("clima", ex); nextWeather = DateTime.Now.AddMinutes(1); }

                try
                {
                    string ql = (QuoteList ?? "").Replace(" ", "").ToUpperInvariant();
                    if (ql != lastQuoteList) nextQuotes = DateTime.MinValue;
                    if (ql != "" && DateTime.Now >= nextQuotes)
                    {
                        nextQuotes = DateTime.Now.AddMinutes(2);
                        lastQuoteList = ql;
                        var d = js.Deserialize<Dictionary<string, object>>(Get("https://economia.awesomeapi.com.br/json/last/" + ql));
                        var list = new List<Quote>();
                        foreach (var code in ql.Split(','))
                        {
                            object o;
                            if (!d.TryGetValue(code.Replace("-", ""), out o)) continue;
                            var q = (Dictionary<string, object>)o;
                            list.Add(new Quote { Code = Convert.ToString(q["code"]), In = Convert.ToString(q["codein"]),
                                Bid = D(q["bid"]), Pct = D(q["pctChange"]) });
                        }
                        Quotes = list; QuoteError = "";
                    }
                    if (ql == "") Quotes = new List<Quote>();
                }
                catch (Exception ex) { QuoteError = "Cotacoes: " + ex.Message; Log.Error("cotacoes", ex); nextQuotes = DateTime.Now.AddMinutes(1); }

                Thread.Sleep(1000);
            }
        }

        static void FetchWeather(JavaScriptSerializer js)
        {
            string url = string.Format(IC,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day" +
                "&hourly=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2", lat, lon);
            var f = js.Deserialize<Dictionary<string, object>>(Get(url));
            var c = (Dictionary<string, object>)f["current"];
            Temp = D(c["temperature_2m"]); Feels = D(c["apparent_temperature"]);
            Humidity = D(c["relative_humidity_2m"]); Code = Convert.ToInt32(c["weather_code"]); IsDay = Convert.ToInt32(c["is_day"]) == 1;
            var daily = (Dictionary<string, object>)f["daily"];
            TMax = D(((ArrayList)daily["temperature_2m_max"])[0]); TMin = D(((ArrayList)daily["temperature_2m_min"])[0]);
            var h = (Dictionary<string, object>)f["hourly"];
            var times = (ArrayList)h["time"]; var temps = (ArrayList)h["temperature_2m"]; var codes = (ArrayList)h["weather_code"];
            var list = new List<HourFc>();
            DateTime now = DateTime.Parse(Convert.ToString(c["time"]), IC);
            for (int i = 0; i < times.Count && list.Count < 6; i++)
            {
                var t = DateTime.Parse(Convert.ToString(times[i]), IC);
                if (t <= now) continue;
                if (list.Count == 0 || (t - list[list.Count - 1].Time).TotalHours >= 2)
                    list.Add(new HourFc { Time = t, Temp = D(temps[i]), Code = Convert.ToInt32(codes[i]) });
            }
            Hours = list; HasWeather = true; WeatherError = "";
        }

        public static string Describe(int code)
        {
            if (code == 0) return "Ceu limpo";
            if (code <= 2) return "Poucas nuvens";
            if (code == 3) return "Nublado";
            if (code == 45 || code == 48) return "Neblina";
            if (code >= 51 && code <= 57) return "Garoa";
            if (code >= 61 && code <= 67) return "Chuva";
            if (code >= 71 && code <= 77) return "Neve";
            if (code >= 80 && code <= 82) return "Pancadas de chuva";
            if (code >= 95) return "Tempestade";
            return "";
        }

        // ---------- icones vetoriais simples
        public static void DrawIcon(Graphics g, int code, bool day, RectangleF r, Color accent, Color fg)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height), x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
            Color sun = Color.FromArgb(255, 200, 40), cloud = Color.FromArgb(225, fg), dark = Color.FromArgb(150, 150, 160);
            bool clouds = code >= 1, heavy = code == 3 || code >= 45;
            if (code <= 2)
            {
                // sol / lua
                float cs = code == 0 ? s * 0.5f : s * 0.4f, cx = code == 0 ? x + s / 2 : x + s * 0.38f, cy = code == 0 ? y + s / 2 : y + s * 0.36f;
                if (day)
                {
                    using (var p = new Pen(sun, Math.Max(2, s / 22)))
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, cx + (float)Math.Cos(a) * cs * 0.65f, cy + (float)Math.Sin(a) * cs * 0.65f,
                                          cx + (float)Math.Cos(a) * cs * 0.85f, cy + (float)Math.Sin(a) * cs * 0.85f);
                        }
                    using (var b = new SolidBrush(sun)) g.FillEllipse(b, cx - cs / 2, cy - cs / 2, cs, cs);
                }
                else
                {
                    using (var b = new SolidBrush(Color.FromArgb(230, 230, 200))) g.FillEllipse(b, cx - cs / 2, cy - cs / 2, cs, cs);
                    using (var b = new SolidBrush(Color.FromArgb(20, 22, 30))) g.FillEllipse(b, cx - cs / 2 + cs * 0.3f, cy - cs / 2 - cs * 0.1f, cs, cs);
                }
            }
            if (clouds && code != 0)
            {
                float cy = code <= 2 ? y + s * 0.45f : y + s * 0.25f;
                using (var b = new SolidBrush(heavy ? dark : cloud))
                {
                    g.FillEllipse(b, x + s * 0.12f, cy + s * 0.18f, s * 0.4f, s * 0.3f);
                    g.FillEllipse(b, x + s * 0.3f, cy, s * 0.42f, s * 0.42f);
                    g.FillEllipse(b, x + s * 0.55f, cy + s * 0.15f, s * 0.33f, s * 0.33f);
                    g.FillRectangle(b, x + s * 0.3f, cy + s * 0.3f, s * 0.42f, s * 0.18f);
                }
            }
            float by = y + s * 0.72f;
            if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82))
                using (var p = new Pen(Color.FromArgb(80, 170, 255), Math.Max(2, s / 18)))
                    for (int i = 0; i < 3; i++) g.DrawLine(p, x + s * (0.32f + i * 0.17f), by, x + s * (0.27f + i * 0.17f), by + s * 0.16f);
            if (code >= 71 && code <= 77)
                using (var b = new SolidBrush(Color.White))
                    for (int i = 0; i < 3; i++) g.FillEllipse(b, x + s * (0.3f + i * 0.17f), by + s * 0.04f, s * 0.08f, s * 0.08f);
            if (code >= 95)
                using (var b = new SolidBrush(sun))
                    g.FillPolygon(b, new[] { new PointF(x + s * 0.5f, by - s * 0.04f), new PointF(x + s * 0.38f, by + s * 0.12f),
                        new PointF(x + s * 0.49f, by + s * 0.12f), new PointF(x + s * 0.42f, by + s * 0.27f), new PointF(x + s * 0.62f, by + s * 0.06f),
                        new PointF(x + s * 0.51f, by + s * 0.06f), new PointF(x + s * 0.58f, by - s * 0.04f) });
            if (code == 45 || code == 48)
                using (var p = new Pen(Color.FromArgb(180, fg), Math.Max(2, s / 20)))
                    for (int i = 0; i < 3; i++) g.DrawLine(p, x + s * (0.18f + i * 0.05f), by + i * s * 0.09f, x + s * (0.82f - i * 0.05f), by + i * s * 0.09f);
            g.SmoothingMode = old;
        }
    }
}
