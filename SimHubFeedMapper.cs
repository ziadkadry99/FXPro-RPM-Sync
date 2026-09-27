using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace User.FXProRpmSync
{
    public enum GapMode
    {
        /// <summary>Race sessions: cars one position ahead/behind in your class. Other sessions: nearest cars on track.</summary>
        Auto,
        /// <summary>Cars one position ahead/behind in your class (race order).</summary>
        RaceClass,
        /// <summary>Cars one position ahead/behind overall (race order).</summary>
        RaceOverall,
        /// <summary>Nearest cars ahead/behind on track, any class or lap.</summary>
        OnTrack,
    }

    public enum DeltaSource { SessionBest, AllTimeBest }

    /// <summary>Settings for driving the wheel's dash values from SimHub.</summary>
    public class FeedSettings
    {
        public bool Enabled = false;
        public GapMode Gaps = GapMode.Auto;
        public DeltaSource Delta = DeltaSource.SessionBest;
        /// <summary>SimPro struct field -> SimHub property path; wins over the built-in mapping for that field.</summary>
        public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills SimPro's _STelemetryData from SimHub's data. SimPro's struct mirrors SimHub's own data model, so fields map
    /// by name; what needs care is units and ranges. Everything below was traced end to end (SimPro 3.1.1
    /// TelemetryData_v2tov1 + packetTelemetryHigh/Low, wheel firmware 1.3.11 widget senders):
    ///  - SimHub converts pressures, temperatures and fuel to the user's display units; SimPro and the wheel expect
    ///    psi, °C and litres (the wheel converts for display itself, from SimPro's unit settings).
    ///  - Values are clamped to what the wire carries (bytes, 10-bit fields, u16).
    /// </summary>
    internal static class SimHubFeedMapper
    {
        private const double KpaPerPsi = 6.89476, BarPerPsi = 0.0689476, LitresPerGallon = 3.78541;

        public static void Fill(SimProTelemetry t, GameData data, PluginManager pm, FeedSettings s, Action<string, Exception> onError)
        {
            t.Clear();
            var d = data.NewData;
            if (d == null || !data.GameRunning)
            {
                t.Set("isGameRunning", false);
                return;
            }

            double fuelToLitres = FuelToLitres(d.FuelUnit);

            Section(onError, "driving", () =>
            {
                // ----- Driving -----
                // speed: km/h, 9 bits on the wire. rpm: 15 bits.
                double maxRpm = d.CarSettings_MaxRPM > 0 ? d.CarSettings_MaxRPM : d.MaxRpm;
                t.Set("speed", Clamp(d.SpeedKmh, 0, 511));
                t.Set("rpm", Clamp(d.Rpms, 0, 32767));
                // SimPro reads the game max RPM from here ("Current Game Max RPM"); the rev lights scale to it.
                t.Set("maxRpm", Clamp(maxRpm, 0, 32767));
                t.Set("rpmPercentage", maxRpm > 0 ? Clamp(d.Rpms / maxRpm * 100, 0, 100) : 0);
                // gear: -1 = R, 0 = N (SimPro sends it +1; the wheel shows 0 as R, 1 as N).
                t.Set("gear", ParseGear(d.Gear));
                t.Set("maxGears", d.CarSettings_MaxGears > 0 ? d.CarSettings_MaxGears : 6);
                // Pedals: % pressed, 0-100. SimHub and SimPro's own readers both use pressed % for clutch
                // (100 - engagement); the wheel draws the clutch bar as 100 - value.
                t.Set("throttle", Clamp(d.Throttle, 0, 100));
                t.Set("brake", Clamp(d.Brake, 0, 100));
                t.Set("clutch", Clamp(d.Clutch, 0, 100));
                t.Set("handbrake", Clamp(d.Handbrake, 0, 100));
                t.Set("engineTorque", d.EngineTorque);
                // Not every game reports these to SimHub; a turning engine counts as running (FXProDashes always sent 1).
                t.Set("isEngineRunning", d.EngineStarted != 0 || d.Rpms > 0);
                t.Set("isEngineIgnitionOn", d.EngineIgnitionOn != 0 || d.Rpms > 0);
                t.Set("engineStarted", d.EngineStarted);
            });

            Section(onError, "aids", () =>
            {
                // ----- Aids -----
                t.Set("isAbsActive", d.ABSActive != 0);
                t.Set("isTcActive", d.TCActive != 0);
                t.Set("isPitLimiterOn", d.PitLimiterOn != 0);
                t.Set("isDrsAvaiable", d.DRSAvailable != 0);
                t.Set("isDrsEnabled", d.DRSEnabled != 0);
                t.Set("absLevel", Clamp(d.ABSLevel, 0, 15));   // lower nibble on the wire
                t.Set("tcLevel", Clamp(d.TCLevel, 0, 255));
                t.Set("engineMap", Clamp(d.EngineMap, 0, 255));
                t.Set("isMapAllowed", d.MapAllowed);
            });

            Section(onError, "timing", () =>
            {
                // ----- Timing -----
                t.Set("completedLaps", Clamp(d.CompletedLaps, 0, 255)); // the wheel's "lap" shows this, as with SimPro
                t.Set("currentLap", d.CurrentLap);
                t.Set("totalLaps", d.TotalLaps);
                t.Set("remainingLaps", d.RemainingLaps);
                t.Set("position", Clamp(d.Position, 0, 255));
                t.Set("playerLeaderboardPosition", d.PlayerLeaderboardPosition);
                t.Set("currentLapTime", Ms(d.CurrentLapTime));             // ms; SimPro splits into min:s:ms
                t.Set("lastLapTime", Ms(d.LastLapTime));
                t.Set("bestLapTime", Ms(d.BestLapTime));
                t.Set("allTimeBest", Ms(d.AllTimeBest));
                t.Set("sessionTimeLeft", Clamp(d.SessionTimeLeft.TotalSeconds, 0, int.MaxValue));
                // Delta: seconds, + = slower. SimPro sends x100 as int16; the wheel shows "%+0.2f" and fills the
                // gain/loss bars over +-5 s.
                double? delta = s.Delta == DeltaSource.AllTimeBest ? d.DeltaToAllTimeBest : d.DeltaToSessionBest;
                t.Set("gainLoss", Clamp(delta ?? 0, -327, 327));
                t.Set("deltaToSessionBest", d.DeltaToSessionBest ?? 0);
                t.Set("deltaToAllTimeBest", d.DeltaToAllTimeBest ?? 0);
                // Flags: SimPro shows the first set flag in the order green, blue, yellow, black, white.
                t.Set("greenFlag", d.Flag_Green != 0);
                t.Set("blueFlag", d.Flag_Blue != 0);
                t.Set("yellowFlag", d.Flag_Yellow != 0);
                t.Set("blackFlag", d.Flag_Black != 0);
                t.Set("whiteFlag", d.Flag_White != 0);
                t.Set("checkeredFlag", d.Flag_Checkered != 0);
                t.Set("orangeFlag", d.Flag_Orange != 0);
                t.Set("isInPit", d.IsInPit != 0);
                t.Set("isInPitLane", d.IsInPitLane != 0);
            });

            Section(onError, "gaps", () =>
            {
                // Gaps: hundredths of a second, positive. The wheel shows gapa as value/100 "%.1f" and gapb as
                // "-%.1f"; both are u16 on the wire (0-655.35 s). SimPro passes gapAhead as is and abs(gapBehind).
                var (ahead, behind) = Gaps(d, s.Gaps);
                t.Set("gapAhead", Clamp(Math.Round(Math.Abs(ahead ?? 0) * 100), 0, 65535));
                t.Set("gapBehind", Clamp(Math.Round(Math.Abs(behind ?? 0) * 100), 0, 65535));
            });

            Section(onError, "fuel", () =>
            {
                // ----- Fuel, brakes, energy -----
                // Fuel: litres, x10 in 10 bits on the wire (max 102.3).
                t.Set("fuel", Clamp(d.Fuel * fuelToLitres, 0, 102.3));
                t.Set("maxFuel", (d.CarSettings_MaxFUEL > 0 ? d.CarSettings_MaxFUEL : d.MaxFuel) * fuelToLitres);
                t.Set("fuelPercent", Clamp(d.FuelPercent, 0, 100));
                // Litres per lap x10 as a byte: the wheel's "fxl" shows value/10 "%.1f" (max 25.5 L/lap). SimHub's
                // Computed.Fuel_LitersPerLap is in the user's fuel unit despite its name.
                double lpl = ToDouble(pm.GetPropertyValue("DataCorePlugin.Computed.Fuel_LitersPerLap")) * fuelToLitres;
                t.Set("fuelPerLap", Clamp(Math.Round(lpl * 10), 0, 255));
                t.Set("fuelLitersPerLap", lpl);
                // Brake bias: % front, x10 in 10 bits (max 102.3).
                t.Set("brakeBias", Clamp(d.BrakeBias, 0, 102.3));
                t.Set("ersMax", d.ERSMax);
                t.Set("ersStored", d.ERSStored);
                t.Set("ersPercent", Clamp(d.ERSPercent, 0, 100));             // wheel "soc"
                t.Set("turbo", Clamp(d.Turbo, 0, 65535));
                t.Set("turboPercent", d.TurboPercent);
                t.Set("maxTurbo", d.MaxTurbo);
            });

            Section(onError, "temperatures", () =>
            {
                // ----- Temperatures (°C) and pressures (psi) -----
                Func<double, double> c = TempToCelsius(d.TemperatureUnit);
                Func<double, double> psi = PressureToPsi(d.TyrePressureUnit);
                t.Set("waterTemperature", Clamp(c(d.WaterTemperature), 0, 255));   // byte
                t.Set("oilTemperature", Clamp(c(d.OilTemperature), 0, 255));       // byte
                t.Set("oilPressure", Clamp(psi(d.OilPressure), 0, 255));           // byte, psi (SimHub uses the tyre pressure unit)
                t.Set("airTemperature", c(d.AirTemperature));
                t.Set("roadTemperature", c(d.RoadTemperature));
                // Corners 0-3 = FL, FR, RL, RR (the wheel's ul, ur, dl, dr).
                SetCorners(t, "brakeTemperature", 0, 1023, c(d.BrakeTemperatureFrontLeft), c(d.BrakeTemperatureFrontRight), c(d.BrakeTemperatureRearLeft), c(d.BrakeTemperatureRearRight));
                SetCorners(t, "tyreTemperature", 0, 255, c(d.TyreTemperatureFrontLeft), c(d.TyreTemperatureFrontRight), c(d.TyreTemperatureRearLeft), c(d.TyreTemperatureRearRight));
                SetCorners(t, "tyreTemperatureInner", 0, 255, c(d.TyreTemperatureFrontLeftInner), c(d.TyreTemperatureFrontRightInner), c(d.TyreTemperatureRearLeftInner), c(d.TyreTemperatureRearRightInner));
                SetCorners(t, "tyreTemperatureMiddle", 0, 255, c(d.TyreTemperatureFrontLeftMiddle), c(d.TyreTemperatureFrontRightMiddle), c(d.TyreTemperatureRearLeftMiddle), c(d.TyreTemperatureRearRightMiddle));
                SetCorners(t, "tyreTemperatureOuter", 0, 255, c(d.TyreTemperatureFrontLeftOuter), c(d.TyreTemperatureFrontRightOuter), c(d.TyreTemperatureRearLeftOuter), c(d.TyreTemperatureRearRightOuter));
                // Pressure: psi, x10 in 10 bits (max 102.3); the wheel converts to bar/kPa itself.
                SetCorners(t, "tyrePressure", 0, 102.3, psi(d.TyrePressureFrontLeft), psi(d.TyrePressureFrontRight), psi(d.TyrePressureRearLeft), psi(d.TyrePressureRearRight));
                // Wear: % remaining (100 = new) in both SimHub and the games SimPro reads; the wheel shows "%d%%".
                SetCorners(t, "tyreWear", 0, 100, d.TyreWearFrontLeft, d.TyreWearFrontRight, d.TyreWearRearLeft, d.TyreWearRearRight);
                SetCorners(t, "tyreDirt", 0, 100, d.TyreDirtFrontLeft, d.TyreDirtFrontRight, d.TyreDirtRearLeft, d.TyreDirtRearRight);
            });

            Section(onError, "motion", () =>
            {
                // ----- Motion -----
                t.Set("pitch", d.OrientationPitch);
                t.Set("roll", d.OrientationRoll);
                t.Set("yaw", d.OrientationYaw);
                t.Set("accelerationSway", d.AccelerationSway ?? 0);
                t.Set("accelerationSurge", d.AccelerationSurge ?? 0);
                t.Set("accelerationHeave", d.AccelerationHeave ?? 0);
                t.Set("sway", d.AccelerationSway ?? 0);
                t.Set("surge", d.AccelerationSurge ?? 0);
                t.Set("heave", d.AccelerationHeave ?? 0);
                t.Set("globalAccelerationG", d.GlobalAccelerationG);
                t.Set("carDamage0", d.CarDamage1);
                t.Set("carDamage1", d.CarDamage2);
                t.Set("carDamage2", d.CarDamage3);
                t.Set("carDamage3", d.CarDamage4);
                t.Set("carDamage4", d.CarDamage5);
            });

            Section(onError, "car", () =>
            {
                // ----- Car settings, names -----
                t.Set("CarSettings_maxRPM", d.CarSettings_MaxRPM);
                t.Set("CarSettings_RedLineRPM", d.CarSettings_RedLineRPM);
                t.Set("CarSettings_maxFUEL", d.CarSettings_MaxFUEL * fuelToLitres);
                t.Set("CarSettings_maxGears", d.CarSettings_MaxGears);
                t.Set("turnIndicatorLeft", d.TurnIndicatorLeft);
                t.Set("turnIndicatorRight", d.TurnIndicatorRight);
                t.Set("trackLength", d.TrackLength);
                t.Set("trackPositionPercent", d.TrackPositionPercent);
                t.Set("opponentsCount", d.OpponentsCount);
                t.Set("isGamePaused", data.GamePaused);
                t.Set("isGameRunning", true);
                t.SetString("carModelString", d.CarModel);
                t.SetString("carId", d.CarId);
                t.SetString("trackNameString", d.TrackName);
                t.SetString("trackId", d.TrackId);
                t.SetString("playerName", d.PlayerName);
                t.SetString("sessionTypeName", d.SessionTypeName);
                t.SetString("flagName", d.Flag_Name);
            });

            Section(onError, "raw", () =>
            {
                // ----- Values only in each game's raw data: built-in lookups, then the user's overrides -----
                foreach (var kv in BuiltInRawFields(data.GameName))
                    SetFromProperty(t, pm, kv.Key, kv.Value);
                if (s.Overrides != null)
                    foreach (var kv in s.Overrides)
                        if (!string.IsNullOrWhiteSpace(kv.Value) && SimProTelemetry.Fields.ContainsKey(kv.Key))
                            SetFromProperty(t, pm, kv.Key, kv.Value.Trim());
            });
        }

        /// <summary>
        /// Race order: gaps (seconds) to the cars one position ahead/behind, from SimHub's GaptoPlayer (= their gap to
        /// the leader minus yours; negative ahead). On track: SimHub's RelativeGapToPlayer to the nearest cars.
        /// Null when there's no such car or the game doesn't provide the gap.
        /// </summary>
        internal static (double? Ahead, double? Behind) Gaps(StatusDataBase d, GapMode mode)
        {
            if (mode == GapMode.Auto)
                mode = (d.SessionTypeName ?? "").IndexOf("race", StringComparison.OrdinalIgnoreCase) >= 0 ? GapMode.RaceClass : GapMode.OnTrack;

            if (mode == GapMode.OnTrack)
            {
                var a = d.OpponentsAheadOnTrack?.FirstOrDefault();
                var b = d.OpponentsBehindOnTrack?.FirstOrDefault();
                return (a?.RelativeGapToPlayer, b?.RelativeGapToPlayer);
            }

            var all = d.Opponents;
            var player = all?.FirstOrDefault(o => o.IsPlayer);
            if (player == null || player.Position <= 0) return (null, null);
            var field = all.Where(o => !o.IsPlayer && o.Position > 0);
            if (mode == GapMode.RaceClass) field = field.Where(o => o.CarClass == player.CarClass);
            var list = field.ToList();
            // Neighbours by overall position within the chosen field, so classes don't need PositionInClass.
            var ahead = list.Where(o => o.Position < player.Position).OrderByDescending(o => o.Position).FirstOrDefault();
            var behind = list.Where(o => o.Position > player.Position).OrderBy(o => o.Position).FirstOrDefault();
            return (ahead?.GaptoPlayer, behind?.GaptoPlayer);
        }

        /// <summary>
        /// Game-specific values SimHub only has in the raw game data. A property that doesn't exist for the current car
        /// or game reads as null and the field stays 0, so these are safe to try.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, string>> BuiltInRawFields(string game)
        {
            switch (game)
            {
                case "AssettoCorsaCompetizione":
                    yield return Kv("tcCut", "DataCorePlugin.GameRawData.Graphics.TCCut");
                    break;
                case "IRacing":
                    const string ir = "DataCorePlugin.GameRawData.Telemetry.";
                    yield return Kv("tcCut", ir + "dcTractionControl2");
                    yield return Kv("frontAntiRollBar", ir + "dcAntiRollFront");
                    yield return Kv("rearAntiRollBar", ir + "dcAntiRollRear");
                    yield return Kv("engineBraking", ir + "dcEngineBraking");
                    yield return Kv("diffEntry", ir + "dcDiffEntry");
                    yield return Kv("diffMiddle", ir + "dcDiffMiddle");
                    yield return Kv("diffExit", ir + "dcDiffExit");
                    yield return Kv("throttleShape", ir + "dcThrottleShape");
                    yield return Kv("ersMode", ir + "dcMGUKDeployMode");
                    break;
            }
        }

        /// <summary>Runs one block of the mapping; a failure leaves that block's fields at 0 instead of dropping the frame.</summary>
        private static void Section(Action<string, Exception> onError, string name, Action fill)
        {
            try { fill(); }
            catch (Exception ex) { onError?.Invoke(name, ex); }
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);

        private static void SetFromProperty(SimProTelemetry t, PluginManager pm, string field, string path)
        {
            object v;
            try { v = pm.GetPropertyValue(path); }
            catch { return; }
            if (v == null) return;
            if (SimProTelemetry.Fields[field].Kind == 's') { t.SetString(field, v.ToString()); return; }
            double x = ToDouble(v);
            if (field.Equals("ersMode", StringComparison.OrdinalIgnoreCase)) x = Clamp(x, 0, 15); // low nibble on the wire
            else if (ByteFields.Contains(field)) x = Clamp(x, 0, 255);
            t.Set(field, x);
        }

        /// <summary>Fields that go to the wheel as a single byte (91 04 / 91 05).</summary>
        private static readonly HashSet<string> ByteFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tcCut", "tcLevel", "engineMap", "ersMode", "ersPercent", "pushToPass", "frontAntiRollBar", "rearAntiRollBar",
            "fuelPerLap", "diffAdjOnThrottle", "throttleShape", "engineBraking", "diffEntry", "diffMiddle", "diffExit",
            "drsLevel", "position", "completedLaps", "waterTemperature", "oilTemperature", "oilPressure",
        };

        private static void SetCorners(SimProTelemetry t, string name, double min, double max, double fl, double fr, double rl, double rr)
        {
            t.Set(name + "0", Clamp(fl, min, max));
            t.Set(name + "1", Clamp(fr, min, max));
            t.Set(name + "2", Clamp(rl, min, max));
            t.Set(name + "3", Clamp(rr, min, max));
        }

        internal static int ParseGear(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return 0;
            if (gear.Equals("R", StringComparison.OrdinalIgnoreCase)) return -1;
            return int.TryParse(gear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? Math.Max(-1, Math.Min(13, g)) : 0;
        }

        private static double Ms(TimeSpan ts) => Clamp(ts.TotalMilliseconds, 0, int.MaxValue);

        internal static Func<double, double> TempToCelsius(string unit)
        {
            switch (unit)
            {
                case "Fahrenheit": return f => (f - 32) / 1.8;
                case "Kelvin": return k => k - 273.15;
                default: return x => x; // "Celcius" (SimHub's spelling)
            }
        }

        internal static Func<double, double> PressureToPsi(string unit)
        {
            switch (unit)
            {
                case "Kpa": return p => p / KpaPerPsi;
                case "Bar": return p => p / BarPerPsi;
                default: return p => p; // "Psi"
            }
        }

        internal static double FuelToLitres(string unit) => unit == "Gallons" ? LitresPerGallon : 1.0;

        private static double Clamp(double v, double min, double max) =>
            double.IsNaN(v) ? min : v < min ? min : v > max ? max : v;

        private static double ToDouble(object v)
        {
            switch (v)
            {
                case null: return 0;
                case bool b: return b ? 1 : 0;
                case TimeSpan ts: return ts.TotalSeconds;
                case IConvertible cv:
                    try { return cv.ToDouble(CultureInfo.InvariantCulture); }
                    catch { return 0; }
                default: return 0;
            }
        }
    }
}
