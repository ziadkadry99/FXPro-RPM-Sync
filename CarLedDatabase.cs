using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace User.FXProRpmSync
{
    /// <summary>A car's real shift lights, from Lovely Car Data (index 0 = redline, 1..N = LEDs).</summary>
    public class CarLedProfile
    {
        public string CarName;
        public int LedNumber;
        public int RedlineBlinkIntervalMs;
        public string[] Colors;                       // length N+1
        public Dictionary<string, int[]> GearRpm;    // gear ("R","N","1"..) -> length N+1
        public string MatchedBy = "exact";

        public string Signature() =>
            $"{LedNumber}|{RedlineBlinkIntervalMs}|{string.Join(",", Colors)}|" +
            string.Join(";", GearRpm.OrderBy(g => g.Key).Select(g => g.Key + ":" + string.Join(",", g.Value)));
    }

    /// <summary>
    /// Per-car rev light data from https://github.com/Lovely-Sim-Racing/lovely-car-data
    /// (CC BY-NC-SA 4.0, Lovely Sim Racing and contributors). Downloaded at runtime and cached on disk.
    /// </summary>
    public class CarLedDatabase
    {
        public const string DataUrl = "https://raw.githubusercontent.com/Lovely-Sim-Racing/lovely-car-data/main/data/";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(24);
        private static readonly TimeSpan CarMaxAge = TimeSpan.FromDays(7);

        private readonly string cacheDir;
        private JObject manifest;
        private DateTime manifestLoadedUtc;

        public CarLedDatabase(string cacheDir)
        {
            this.cacheDir = cacheDir;
            Directory.CreateDirectory(Path.Combine(cacheDir, "cars"));
        }

        /// <summary>Returns null when the car isn't in the database (or it can't be reached and nothing is cached).</summary>
        public async Task<CarLedProfile> Find(string gameName, string carId, string carModel)
        {
            var simId = (gameName ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(carId)) return null;

            var m = await GetManifest().ConfigureAwait(false);
            if (!(m?["cars"]?[simId] is JArray cars)) return null;

            var entry =
                cars.FirstOrDefault(c => (string)c["carId"] == carId) ??
                cars.FirstOrDefault(c => string.Equals((string)c["carId"], carId, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                var exact = await Load(entry).ConfigureAwait(false);
                if (exact != null) exact.MatchedBy = "exact";
                return exact;
            }

            return await FindSibling(cars, carId).ConfigureAwait(false);
        }

        /// <summary>
        /// Games like LMU name cars after the team/entry ("LMP2_DKR Engineering 2026_3"), so a new season's entry is
        /// often missing while identical cars are listed. Use the listed cars of the same series ("LMP2" vs
        /// "LMP2_ELMS") only when they all share one light setup (spec classes); otherwise only the same team's
        /// entries, again only when they agree. Never guesses between different setups.
        /// </summary>
        private async Task<CarLedProfile> FindSibling(JArray cars, string carId)
        {
            var (series, team) = SplitCarId(carId);
            if (series == null) return null;

            var sameSeries = cars.Where(c => SplitCarId((string)c["carId"]).Series == series).ToList();
            var profile = await Uniform(sameSeries).ConfigureAwait(false);
            if (profile != null) { profile.MatchedBy = $"same setup as all {series} cars"; return profile; }

            if (string.IsNullOrEmpty(team)) return null;
            var sameTeam = sameSeries.Where(c => SplitCarId((string)c["carId"]).Team == team).ToList();
            profile = await Uniform(sameTeam).ConfigureAwait(false);
            if (profile != null) profile.MatchedBy = $"same setup as {series} {team}";
            return profile;
        }

        /// <summary>The shared profile when every entry has identical light data; null if empty or they differ.</summary>
        private async Task<CarLedProfile> Uniform(List<JToken> entries)
        {
            if (entries.Count == 0) return null;
            CarLedProfile first = null;
            string firstSig = null;
            foreach (var e in entries)
            {
                var p = await Load(e).ConfigureAwait(false);
                if (p == null) return null;
                var sig = p.Signature();
                if (first == null) { first = p; firstSig = sig; }
                else if (sig != firstSig) return null;
            }
            return first;
        }

        /// <summary>"LMP2_ELMS_DKR Engineering 2026_3" -> ("LMP2_ELMS", "dkr engineering"); no series prefix -> (null, null).</summary>
        public static (string Series, string Team) SplitCarId(string carId)
        {
            var parts = (carId ?? "").Split('_');
            int i = 0;
            while (i < parts.Length - 1 && System.Text.RegularExpressions.Regex.IsMatch(parts[i], "^[A-Za-z0-9]+$")) i++;
            if (i == 0) return (null, null);
            var team = System.Text.RegularExpressions.Regex.Replace(parts[i], @"\s+(19|20)\d\d\b.*$", "").Trim().ToLowerInvariant();
            return (string.Join("_", parts.Take(i)), team);
        }

        private async Task<CarLedProfile> Load(JToken entry)
        {
            var path = (string)entry["path"];
            var json = await GetCached("cars/" + path.Replace('/', '_'), DataUrl + path, CarMaxAge).ConfigureAwait(false);
            return json == null ? null : Parse(JObject.Parse(json));
        }

        private async Task<JObject> GetManifest()
        {
            if (manifest != null && DateTime.UtcNow - manifestLoadedUtc < ManifestMaxAge) return manifest;
            var json = await GetCached("manifest.json", DataUrl + "manifest.json", ManifestMaxAge).ConfigureAwait(false);
            if (json != null)
            {
                manifest = JObject.Parse(json);
                manifestLoadedUtc = DateTime.UtcNow;
            }
            return manifest;
        }

        /// <summary>Fresh cache -> cache; otherwise download (falling back to a stale cache when offline).</summary>
        private async Task<string> GetCached(string name, string url, TimeSpan maxAge)
        {
            var file = Path.Combine(cacheDir, name);
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < maxAge)
                return File.ReadAllText(file, Encoding.UTF8);
            try
            {
                var text = await Http.GetStringAsync(url).ConfigureAwait(false);
                JToken.Parse(text); // don't cache garbage
                File.WriteAllText(file, text, Encoding.UTF8);
                return text;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[FXProRpmSync] car data download failed ({url}): {ex.Message}");
                return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
            }
        }

        private static CarLedProfile Parse(JObject d)
        {
            var n = d.Value<int?>("ledNumber") ?? 0;
            var colors = (d["ledColor"] as JArray)?.Select(c => (string)c).ToArray();
            var gears = (d["ledRpm"] as JArray)?.FirstOrDefault() as JObject;
            if (n <= 0 || colors == null || colors.Length < n + 1 || gears == null) return null;

            var gearRpm = new Dictionary<string, int[]>();
            foreach (var g in gears.Properties())
            {
                var rpm = (g.Value as JArray)?.Select(v => (int)Math.Round((double)v)).ToArray();
                if (rpm != null && rpm.Length >= n + 1) gearRpm[g.Name] = rpm;
            }
            if (gearRpm.Count == 0) return null;

            return new CarLedProfile
            {
                CarName = (string)d["carName"],
                LedNumber = n,
                RedlineBlinkIntervalMs = d.Value<int?>("redlineBlinkInterval") ?? 0,
                Colors = colors,
                GearRpm = gearRpm,
            };
        }
    }
}
