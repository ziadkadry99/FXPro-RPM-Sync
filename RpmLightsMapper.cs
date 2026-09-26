using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace User.FXProRpmSync
{
    /// <summary>
    /// Converts rev light layouts to and from SimPro "rpm_lights" parts.
    ///
    /// SimPro model (reverse engineered from SimPro 3.2.2):
    ///   max_rpm_source : 0 = read max RPM from game, 1 = custom (user) max RPM
    ///   selected_mode  : 0 = thresholds as %, 1 = thresholds as RPM ("lights_rpm" list instead of "lights")
    ///   rpm_mode       : 0 = one curve for all gears, 1 = advanced/per gear (one entry per gear in the list)
    ///   lights[i].value[led]     : threshold in % mode, on a FIXED 0..20000 scale (= 0..100.00% of max RPM),
    ///                              0 = LED unused. max_rpm does not change this scale (verified on FX Pro).
    ///   lights[i].max_rpm        : the custom max RPM (used when max_rpm_source = 1)
    ///   lights[i].redline        : flash stage, telemtery_item.value.threshold on the same 0..20000 scale
    ///   lights[i].rpm_redlines[] : extra redline stages, same scale
    ///
    /// Strategy: everything is built as an RpmLayout (real RPM), then converted by ToSimPro, keeping the rest of
    /// the selected preset (effects, brightness) untouched.
    /// </summary>
    public static class RpmLightsMapper
    {
        public const int SourceGame = 0;
        public const int SourceUser = 1;
        public const int ModePercentage = 0;
        public const int PercentScale = 20000;

        /// <summary>
        /// Fraction of max RPM -> threshold units (0..20000). The wheel lights LED i when rpm >= value[i] / 20000 *
        /// (the game max RPM SimPro reads), regardless of max_rpm / max_rpm_source (verified on FX Pro in LMU), so
        /// fractions must be of exactly that game max. The wheel only resolves whole percents and truncates
        /// (measured: 72.7% fired at 72%, 84.85% at 84%, 96.9% at 96%), so snap to the nearest whole percent to
        /// land within +-0.5% of the target instead of up to 1% early.
        /// </summary>
        public static int ToWheelPercent(double fraction) =>
            (int)Math.Round(Math.Min(Math.Max(fraction, 0), 1.0) * 100) * (PercentScale / 100);

        private static int ScaleMax(double gameMaxRpm) => (int)Math.Round(Math.Min(Math.Max(gameMaxRpm, 1), 20000));

        public const int WheelLeds = 15;
        public const int RpmModeDefault = 0;
        public const int RpmModeAdvanced = 1;
        public static readonly string[] SimProGears = { "R", "N", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" };

        /// <summary>SimPro redline "Blinking Delay" units per millisecond of the car's blink interval.</summary>
        public static double BlinkMsPerUnit = 50;

        /// <summary>
        /// On the FX Pro the "rpm_redlines" stage list is what actually drives the redline effect (their "enabled"
        /// flag is ignored), so the shift flash goes there; pct 0 = no flash. The single "redline" item is kept in
        /// sync for SimPro's UI.
        /// </summary>
        private static void SetShiftStage(JObject e, int pct, string color, int blinkUnits)
        {
            var stageProto = (e["rpm_redlines"] as JArray)?.OfType<JObject>().FirstOrDefault() ?? e["redline"] as JObject;
            var stages = new JArray();
            if (stageProto != null && pct > 0 && color != null)
                stages.Add(MakeStage(stageProto, pct, color, blinkUnits));
            e["rpm_redlines"] = stages;

            if (e["redline"] is JObject redline)
            {
                var main = stages.LastOrDefault() as JObject;
                if (main != null)
                {
                    redline["light"] = main["light"].DeepClone();
                    redline["telemtery_item"] = main["telemtery_item"].DeepClone();
                }
                redline["enabled"] = main != null;
            }
        }

        /// <summary>
        /// Turns a layout (real RPM) into SimPro's rpm_lights, keeping the template preset's other settings
        /// (effects, brightness, ...). Thresholds are % of the game max RPM SimPro reads (see ToWheelPercent).
        /// Per-gear curves are only used when allowed (SimPro "Advanced" mode; the FX Pro doesn't support it).
        /// </summary>
        public static JObject ToSimPro(JObject template, RpmLayout layout, double gameMaxRpm, bool allowPerGear)
        {
            var part = (JObject)template.DeepClone();
            if (!(part["lights"] is JArray lights) || !(lights.FirstOrDefault() is JObject baseEntry)) return part;

            int newMax = ScaleMax(gameMaxRpm);
            int Pct(int rpm) => rpm <= 0 ? 0 : Math.Max(ToWheelPercent(rpm / (double)newMax), PercentScale / 100);

            JObject Entry(string mode, int[] rpm, int flashRpm)
            {
                var e = (JObject)baseEntry.DeepClone();
                e["mode"] = mode;
                e["value"] = new JArray(rpm.Select(Pct));
                e["color"] = new JArray(rpm.Select((r, i) => r <= 0 ? LedPalette.Off : layout.Colors[i]));
                e["max_rpm"] = newMax;
                SetShiftStage(e, Pct(flashRpm), layout.FlashColor, layout.FlashBlinkUnits);
                return e;
            }

            bool perGear = allowPerGear && layout.Gears != null && layout.Gears.Count > 0;
            var newLights = new JArray { Entry("X", layout.Rpm, layout.FlashRpm) };
            if (perGear)
                foreach (var gear in SimProGears)
                {
                    var g = layout.Gears.TryGetValue(gear, out var c) ? c : new GearCurve { Rpm = layout.Rpm, FlashRpm = layout.FlashRpm };
                    newLights.Add(Entry(gear, g.Rpm, g.FlashRpm));
                }

            part["lights"] = newLights;
            part["rpm_mode"] = perGear ? RpmModeAdvanced : RpmModeDefault;
            part["max_rpm_source"] = SourceGame;
            part["selected_mode"] = ModePercentage;
            return part;
        }

        /// <summary>A pattern choice (including "your SimPro preset") as real RPM, with the sequence completing at shiftRpm.</summary>
        public static RpmLayout FromStyle(FallbackStyle style, JObject presetTemplate, double shiftRpm) =>
            style.Pattern == PatternKind.SimProPreset
                ? FromPreset(presetTemplate, shiftRpm)
                : RpmLayout.FromPattern(LedPatterns.Build(style), shiftRpm, DefaultBlinkUnits);

        /// <summary>The preset's own LED pattern with its redline anchored at shiftRpm, and no flash (a guess).</summary>
        public static RpmLayout FromPreset(JObject template, double shiftRpm) =>
            RpmLayout.FromPattern(PresetLayout(template), shiftRpm, 0);

        public const int DefaultBlinkUnits = 2;

        /// <summary>The selected SimPro preset's LED pattern as a layout (fractions of its redline stage), for previews.</summary>
        public static LedLayout PresetLayout(JObject template)
        {
            var layout = new LedLayout();
            var e = (template?["lights"] as JArray)?.FirstOrDefault() as JObject;
            var values = e?["value"] as JArray;
            var colors = e?["color"] as JArray;
            if (values == null) return layout;

            double anchor = e.SelectToken("redline.telemtery_item.value")?.Value<double?>("threshold") ?? 0;
            if (anchor <= 0) anchor = values.Select(v => (double)v).DefaultIfEmpty(0).Max();
            for (int i = 0; i < WheelLeds && i < values.Count; i++)
            {
                double v = (double)values[i];
                var c = (string)colors?[i];
                bool off = v <= 0 || anchor <= 0 || ToSimProColor(c) == null;
                layout.Fractions[i] = off ? 0 : v / anchor;
                layout.Colors[i] = off ? LedPalette.Off : c;
            }
            return layout;
        }

        /// <summary>A redline stage: all 15 LEDs in one color at pct (0..20000), solid (blinkUnits 0) or blinking.</summary>
        private static JObject MakeStage(JObject proto, int pct, string color, int blinkUnits)
        {
            var st = (JObject)proto.DeepClone();
            st["enabled"] = true;
            if (st.SelectToken("telemtery_item.value") is JObject v)
            {
                v["threshold"] = pct;
                v["max_value"] = PercentScale;
            }
            if (st["light"] is JObject light)
            {
                var effect = blinkUnits > 0 ? "breath" : "mono";
                light["active_effect"] = effect;
                if (light.SelectToken("configs." + effect) is JObject cfg)
                {
                    cfg["foregroud_color"] = new JArray(Enumerable.Repeat(color, WheelLeds));
                    cfg["select_lights"] = new JArray(Enumerable.Range(0, WheelLeds));
                    if (blinkUnits > 0) cfg["interval"] = blinkUnits;
                }
            }
            return st;
        }

        // The FX Pro (an SimPro "old device") only displays SimPro's LED palette; any other color is shown as off.
        private static readonly (double Hue, string Hex)[] PaletteHues =
        {
            (340, "#ff0054"), // red
            (25,  "#ff6c00"), // orange
            (59,  "#fffd51"), // yellow
            (151, "#00ff84"), // green
            (180, "#00fffc"), // cyan
            (239, "#0006ff"), // blue
            (263, "#6000ff"), // purple
        };
        public const string PaletteWhite = "#eeeeee";

        /// <summary>
        /// "#AARRGGBB" / "#RRGGBB" / HTML color name -> nearest SimPro palette color;
        /// null when transparent or (near) black (= LED off).
        /// </summary>
        public static string ToSimProColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            System.Windows.Media.Color c;
            try { c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.Trim()); }
            catch { return null; }

            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
            if (c.A == 0 || max < 0.15) return null;
            if (delta / max < 0.25) return PaletteWhite;

            double hue = max == r ? 60 * (((g - b) / delta + 6) % 6)
                       : max == g ? 60 * ((b - r) / delta + 2)
                       : 60 * ((r - g) / delta + 4);
            double HueDistance(double h) { var d = Math.Abs(h - hue) % 360; return d > 180 ? 360 - d : d; }
            return PaletteHues.OrderBy(p => HueDistance(p.Hue)).First().Hex;
        }

        /// <summary>
        /// Numeric fingerprint of the parts of rpm_lights this plugin changes. Used to tell
        /// "still what we (or the original preset) set" apart from "user edited it in SimPro".
        /// </summary>
        public static string Fingerprint(JObject part)
        {
            var parts = new List<string> { "src=" + part.Value<int?>("max_rpm_source"), "gears=" + part.Value<int?>("rpm_mode") };
            foreach (var listName in new[] { "lights", "lights_rpm" })
            {
                if (!(part[listName] is JArray list)) continue;
                foreach (var e in list.OfType<JObject>())
                {
                    var vals = (e["value"] as JArray)?.Select(v => ((double)v).ToString("0")) ?? Enumerable.Empty<string>();
                    parts.Add($"{listName}:{e.Value<string>("mode")}:{e.Value<double?>("max_rpm"):0}:" +
                              $"{e.SelectToken("redline.telemtery_item.value.threshold")?.Value<double>():0}:" + string.Join(",", vals) +
                              ":" + string.Join(",", (e["color"] as JArray)?.Select(c => ((string)c)?.ToLowerInvariant()) ?? Enumerable.Empty<string>()) +
                              ":stages=" + string.Join(",", (e["rpm_redlines"] as JArray)?.Select(st =>
                                  $"{st.SelectToken("telemtery_item.value.threshold")?.Value<double>():0}/{st.SelectToken("light.active_effect")}")
                                  ?? Enumerable.Empty<string>()));
                }
            }
            return string.Join("|", parts);
        }
    }
}
