using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace User.FXProRpmSync
{
    public class FXProRpmSyncSettings
    {
        public bool Enabled = true;

        /// <summary>Use each car's real shift lights (Lovely Car Data) when available; otherwise rescale the preset.</summary>
        public bool UseCarDatabase = true;
        /// <summary>How cars without rev-light data are shown.</summary>
        public FallbackStyle Fallback = new FallbackStyle();

        public string SimProUrl = "http://127.0.0.1:4010/simpro/api/v3";

        /// <summary>Per-car adjustments, keyed by "Game | CarId".</summary>
        public Dictionary<string, CarOverride> Overrides = new Dictionary<string, CarOverride>();

        /// <summary>preset_uuid -> original rpm_lights part (JSON) as it was before this plugin touched it.</summary>
        public Dictionary<string, string> Originals = new Dictionary<string, string>();
    }

    [PluginDescription("Keeps the Simagic FX Pro's rev lights matched to the car you're driving, through SimPro Manager")]
    [PluginAuthor("ziadkadry99")]
    [PluginName("FXPro RPM Sync")]
    public class FXProRpmSyncPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string RpmPart = "rpm_lights";
        private const int RpmPartId = 1;

        public FXProRpmSyncSettings Settings;
        public PluginManager PluginManager { get; set; }
        public System.Windows.Media.ImageSource PictureIcon => icon ?? (icon = LoadIcon());
        private System.Windows.Media.ImageSource icon;
        public string LeftMenuTitle => "FXPro RPM Sync";

        private readonly SimProClient simPro = new SimProClient();
        private CarLedDatabase carDb;
        private readonly Dictionary<string, CarLedProfile> profileCache = new Dictionary<string, CarLedProfile>();
        private readonly object sync = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private CancellationTokenSource cts;
        private Task worker;

        // Latest wanted target, written by DataUpdate, consumed by the worker.
        private Target pending;
        private Target lastRequested;

        // Worker state
        private SimProClient.Wheel wheel;
        private string modifiedPresetUuid;
        private Target appliedTarget;
        private double appliedScaleMax;
        private DateTime nextMaxCheckUtc;
        // Recent fingerprints we pushed, per preset. SimPro's read-back lags writes, so a read can return any of
        // these; they must never be mistaken for the user editing the preset.
        private readonly Dictionary<string, List<string>> appliedFingerprints = new Dictionary<string, List<string>>();

        public string Status { get; private set; } = "Idle";
        public string CurrentCar { get; private set; } = "";
        public double AppliedMaxRpm { get; private set; }
        public double AppliedRedline { get; private set; }
        public string LightsSource { get; private set; } = "";

        // The current car, for the settings page's override editor.
        public string CurrentCarKey { get; private set; }
        public string CurrentGame { get; private set; }
        public string CurrentCarId { get; private set; }
        public string CurrentCarName { get; private set; }
        /// <summary>The current car's lights before any override (what the plugin would show without one).</summary>
        public RpmLayout CurrentBaseLayout { get; private set; }
        public string CurrentBaseSource { get; private set; }

        private class Target
        {
            public string CarKey;
            public string GameName;
            public string CarId;
            public string CarModel;
            public double MaxRpm;
            public double Redline;
            public bool Restore;

            public bool SameAs(Target o) =>
                o != null && o.Restore == Restore && o.CarKey == CarKey &&
                Math.Abs(o.MaxRpm - MaxRpm) < 100 && Math.Abs(o.Redline - Redline) < 50;
        }

        public void Init(PluginManager pluginManager)
        {
            MigrateOldSettings();
            Settings = this.ReadCommonSettings("GeneralSettings", () => new FXProRpmSyncSettings());
            if (Settings.Originals == null) Settings.Originals = new Dictionary<string, string>();
            if (Settings.Fallback == null) Settings.Fallback = new FallbackStyle();
            if (Settings.Overrides == null) Settings.Overrides = new Dictionary<string, CarOverride>();
            simPro.BaseUrl = Settings.SimProUrl;
            carDb = new CarLedDatabase(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "Common", "FXProRpmSync"));

            this.AttachDelegate("Status", () => Status);
            this.AttachDelegate("CurrentCar", () => CurrentCar);
            this.AttachDelegate("AppliedMaxRpm", () => AppliedMaxRpm);
            this.AttachDelegate("AppliedRedline", () => AppliedRedline);
            this.AttachDelegate("LightsSource", () => LightsSource);

            this.AddAction("ReapplyNow", (a, b) => Reapply());
            this.AddAction("RestoreOriginal", (a, b) => RequestRestore());
            // Map these to wheel buttons to tune the current car while driving (saved as an offset override).
            this.AddAction("CurrentCarLightsLater", (a, b) => NudgeCurrentCar(+NudgeStepRpm));
            this.AddAction("CurrentCarLightsEarlier", (a, b) => NudgeCurrentCar(-NudgeStepRpm));
            this.AttachDelegate("CurrentCarOverride", () =>
                CurrentCarKey != null && Settings.Overrides.TryGetValue(CurrentCarKey, out var o) ? o.Summary : "");

            cts = new CancellationTokenSource();
            worker = Task.Run(() => WorkerLoop(cts.Token));
            SimHub.Logging.Current.Info("[FXProRpmSync] started");
        }

        private static System.Windows.Media.ImageSource LoadIcon()
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = typeof(FXProRpmSyncPlugin).Assembly.GetManifestResourceStream("User.FXProRpmSync.menu-icon.png");
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>The plugin was called "Simagic RPM Sync" before v0.1; carry its settings (overrides, captured originals) over.</summary>
        private static void MigrateOldSettings()
        {
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "Common");
                var oldFile = System.IO.Path.Combine(dir, "SimagicRpmSyncPlugin.GeneralSettings.json");
                var newFile = System.IO.Path.Combine(dir, "FXProRpmSyncPlugin.GeneralSettings.json");
                if (System.IO.File.Exists(oldFile) && !System.IO.File.Exists(newFile))
                {
                    System.IO.File.Copy(oldFile, newFile);
                    SimHub.Logging.Current.Info("[FXProRpmSync] migrated settings from Simagic RPM Sync");
                }
            }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[FXProRpmSync] settings migration failed: " + ex.Message); }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!Settings.Enabled || !data.GameRunning || data.NewData == null) return;
            var d = data.NewData;

            double max = d.CarSettings_MaxRPM > 0 ? d.CarSettings_MaxRPM : d.MaxRpm;
            double red = d.CarSettings_RedLineRPM > 0 ? d.CarSettings_RedLineRPM : d.Redline;
            if (string.IsNullOrEmpty(d.CarId)) return;

            var t = new Target
            {
                CarKey = data.GameName + " | " + d.CarId,
                GameName = data.GameName,
                CarId = d.CarId,
                CarModel = d.CarModel,
                MaxRpm = max,
                Redline = red,
            };
            lock (sync)
            {
                if (t.SameAs(lastRequested)) return;
                lastRequested = t;
                pending = t;
            }
            wake.Set();
        }

        public void Reapply()
        {
            lock (sync) { lastRequested = null; }
        }

        public void SaveSettings() => this.SaveCommonSettings("GeneralSettings", Settings);

        // ---------- Per-car overrides ----------

        public const int NudgeStepRpm = 50;

        public CarOverride GetOverride(string carKey)
        {
            lock (sync) return carKey != null && Settings.Overrides.TryGetValue(carKey, out var o) ? o.Clone() : null;
        }

        public List<CarOverride> AllOverrides()
        {
            lock (sync) return Settings.Overrides.Values.Select(o => o.Clone()).OrderBy(o => o.Game).ThenBy(o => o.CarName).ToList();
        }

        public void SaveOverride(CarOverride o)
        {
            o.UpdatedUtc = DateTime.UtcNow;
            lock (sync) Settings.Overrides[o.CarKey] = o.Clone();
            SaveSettings();
            if (o.CarKey == CurrentCarKey) Reapply();
        }

        public void DeleteOverride(string carKey)
        {
            bool removed;
            lock (sync) removed = Settings.Overrides.Remove(carKey);
            if (!removed) return;
            SaveSettings();
            if (carKey == CurrentCarKey) Reapply();
        }

        /// <summary>A new override for the current car, starting from its current lights.</summary>
        public CarOverride NewOverrideForCurrentCar(OverrideKind kind) =>
            CurrentCarKey == null ? null : new CarOverride
            {
                CarKey = CurrentCarKey, Game = CurrentGame, CarId = CurrentCarId, CarName = CurrentCarName,
                Kind = kind, Custom = kind == OverrideKind.Custom ? CurrentBaseLayout?.Clone() : null,
                Style = kind == OverrideKind.Pattern ? Settings.Fallback.Clone() : null,
            };

        public void NudgeCurrentCar(int deltaRpm)
        {
            var o = GetOverride(CurrentCarKey) ?? NewOverrideForCurrentCar(OverrideKind.Offset);
            if (o == null) return;
            if (o.Kind == OverrideKind.Custom && o.Custom != null) o.Custom = o.Custom.Offset(deltaRpm);
            else o.OffsetRpm += deltaRpm;
            SaveOverride(o);
        }

        /// <summary>The selected SimPro preset's original rpm_lights (as captured), for the settings page preview.</summary>
        public JObject PresetTemplate
        {
            get
            {
                lock (sync)
                {
                    string json = null;
                    if (modifiedPresetUuid != null) Settings.Originals.TryGetValue(modifiedPresetUuid, out json);
                    json = json ?? Settings.Originals.Values.FirstOrDefault();
                    return json == null ? null : JObject.Parse(json);
                }
            }
        }

        public void RequestRestore()
        {
            lock (sync)
            {
                pending = new Target { Restore = true };
                lastRequested = pending;
            }
            wake.Set();
        }

        public void ForgetOriginals()
        {
            lock (sync) { Settings.Originals.Clear(); appliedFingerprints.Clear(); }
            this.SaveCommonSettings("GeneralSettings", Settings);
            Status = "Forgot saved originals; the next car change captures the current preset as original";
        }

        public void End(PluginManager pluginManager)
        {
            cts?.Cancel();
            wake.Set();
            try { worker?.Wait(2000); } catch { }

            // Leave SimPro as we found it.
            try { RestoreAsync().Wait(3000); } catch (Exception ex) { SimHub.Logging.Current.Warn("[FXProRpmSync] restore on exit failed: " + ex.Message); }
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        private async Task WorkerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                wake.WaitOne(1000);
                if (ct.IsCancellationRequested) break;

                Target t;
                lock (sync) { t = pending; pending = null; }
                if (t == null) t = await CheckGameMaxChanged().ConfigureAwait(false);
                if (t == null) continue;

                try
                {
                    if (t.Restore) await RestoreAsync().ConfigureAwait(false);
                    else await ApplyAsync(t).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    wheel = null; // rediscover next time (SimPro restarted / wheel swapped)
                    Status = "Error: " + ex.Message;
                    SimHub.Logging.Current.Warn("[FXProRpmSync] " + ex);
                    lock (sync) { if (pending == null) lastRequested = null; } // retry on next frame
                    await Task.Delay(3000, ct).ContinueWith(_ => { }).ConfigureAwait(false);
                }
            }
        }

        /// <summary>SimPro can pick up the new car's max RPM a moment after SimHub reports the car change.</summary>
        private async Task<Target> CheckGameMaxChanged()
        {
            var t = appliedTarget;
            if (t == null || DateTime.UtcNow < nextMaxCheckUtc) return null;
            nextMaxCheckUtc = DateTime.UtcNow.AddSeconds(3);
            try
            {
                var max = await simPro.GetGameMaxRpm().ConfigureAwait(false);
                return max > 0 && Math.Abs(max - appliedScaleMax) >= 1 ? t : null;
            }
            catch { return null; }
        }

        private async Task<SimProClient.Wheel> GetWheel()
        {
            if (wheel == null)
            {
                wheel = await simPro.FindWheel().ConfigureAwait(false);
                if (wheel == null) throw new Exception("No Simagic wheel found in SimPro Manager");
            }
            return wheel;
        }

        private async Task ApplyAsync(Target t)
        {
            var w = await GetWheel().ConfigureAwait(false);
            var sel = await simPro.GetSelectedPreset(w).ConfigureAwait(false);
            var presetUuid = (string)sel["preset_uuid"];
            var current = sel.SelectToken("config.rpm_lights.1") as JObject
                          ?? throw new Exception("Selected preset has no rpm_lights");

            var original = GetOrCaptureOriginal(presetUuid, current);

            // The wheel scales % thresholds by the game max RPM *SimPro* reads, so use that when we can.
            var simProMax = await simPro.GetGameMaxRpm().ConfigureAwait(false);
            var scaleMax = simProMax > 0 ? simProMax : t.MaxRpm;

            CarLedProfile profile = null;
            if (Settings.UseCarDatabase && !profileCache.TryGetValue(t.CarKey, out profile))
            {
                profile = await carDb.Find(t.GameName, t.CarId, t.CarModel).ConfigureAwait(false);
                profileCache[t.CarKey] = profile;
            }

            // 1) The car's lights in real RPM, from the best source available.
            RpmLayout layout;
            string source;
            if (profile != null)
            {
                layout = RpmLayout.FromProfile(profile, includeGears: !w.OldDevice);
                source = $"car database, {profile.MatchedBy} ({profile.LedNumber} LEDs" + (layout.Gears != null ? ", per gear)" : ")");
            }
            else if (t.Redline > 0)
            {
                var style = Settings.Fallback;
                layout = RpmLightsMapper.FromStyle(style, original, t.Redline);
                source = $"not in car database: {LedPatterns.Catalog.First(c => c.Kind == style.Pattern).Title}, shift point = SimHub redline";
            }
            else return; // SimHub hasn't learned this car's RPM range yet

            CurrentCarKey = t.CarKey;
            CurrentGame = t.GameName;
            CurrentCarId = t.CarId;
            CurrentCarName = string.IsNullOrEmpty(t.CarModel) ? t.CarId : t.CarModel;
            CurrentBaseLayout = layout;
            CurrentBaseSource = source;

            // 2) The user's per-car override, if any.
            var ov = GetOverride(t.CarKey);
            if (ov != null)
            {
                layout = ov.Apply(layout, original) ?? layout;
                source += " + override (" + ov.Summary + ")";
            }
            LightsSource = source;

            // 3) To SimPro's percent-of-game-max settings.
            if (scaleMax <= 0) return;
            var mapped = RpmLightsMapper.ToSimPro(original, layout, scaleMax, allowPerGear: !w.OldDevice);

            await simPro.SetPart(w, presetUuid, RpmPart, RpmPartId, mapped).ConfigureAwait(false);
            RememberApplied(presetUuid, mapped);
            modifiedPresetUuid = presetUuid;
            appliedTarget = t;
            appliedScaleMax = scaleMax;

            CurrentCar = t.CarKey;
            AppliedMaxRpm = scaleMax;
            AppliedRedline = layout.ShiftRpm;
            Status = $"Applied {t.CarKey} from {LightsSource}: redline {AppliedRedline:0} / max {AppliedMaxRpm:0} rpm";
            SimHub.Logging.Current.Info("[FXProRpmSync] " + Status);
        }

        private void RememberApplied(string presetUuid, JObject part)
        {
            lock (sync)
            {
                if (!appliedFingerprints.TryGetValue(presetUuid, out var list))
                    appliedFingerprints[presetUuid] = list = new List<string>();
                list.Add(RpmLightsMapper.Fingerprint(part));
                if (list.Count > 20) list.RemoveAt(0);
            }
        }

        private JObject GetOrCaptureOriginal(string presetUuid, JObject current)
        {
            lock (sync)
            {
                var fp = RpmLightsMapper.Fingerprint(current);
                Settings.Originals.TryGetValue(presetUuid, out var saved);
                appliedFingerprints.TryGetValue(presetUuid, out var applied);

                bool isOurs = applied != null && applied.Contains(fp);
                bool isOriginal = saved != null && RpmLightsMapper.Fingerprint(JObject.Parse(saved)) == fp;

                // First time we see this preset, or the user changed it in SimPro since: current is the new baseline.
                // (After a SimHub restart 'applied' is empty, so a leftover modified preset is only
                //  re-captured if we never saved an original for it.)
                if (saved == null || (!isOurs && !isOriginal && applied != null))
                {
                    saved = current.ToString(Newtonsoft.Json.Formatting.None);
                    Settings.Originals[presetUuid] = saved;
                    this.SaveCommonSettings("GeneralSettings", Settings);
                    SimHub.Logging.Current.Info("[FXProRpmSync] captured original rpm_lights for preset " + presetUuid);
                }
                return JObject.Parse(saved);
            }
        }

        private async Task RestoreAsync()
        {
            var presetUuid = modifiedPresetUuid;
            if (presetUuid == null) return;
            string saved;
            lock (sync) Settings.Originals.TryGetValue(presetUuid, out saved);
            if (saved == null) return;

            var w = await GetWheel().ConfigureAwait(false);
            await simPro.SetPart(w, presetUuid, RpmPart, RpmPartId, JObject.Parse(saved)).ConfigureAwait(false);
            RememberApplied(presetUuid, JObject.Parse(saved));
            modifiedPresetUuid = null;
            appliedTarget = null;
            Status = "Restored original preset RPM lights";
            SimHub.Logging.Current.Info("[FXProRpmSync] " + Status);
        }
    }
}
