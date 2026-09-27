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
        /// <summary>
        /// For cars whose data has a different curve per gear, push the current gear's curve on every gear change
        /// (the FX Pro has no per-gear mode). Off: one curve for all gears.
        /// </summary>
        public bool LiveGearCurves = true;
        /// <summary>How cars without rev-light data are shown.</summary>
        public FallbackStyle Fallback = new FallbackStyle();

        public string SimProUrl = "http://127.0.0.1:4010/simpro/api/v3";

        /// <summary>Per-car adjustments, keyed by "Game | CarId".</summary>
        public Dictionary<string, CarOverride> Overrides = new Dictionary<string, CarOverride>();

        /// <summary>preset_uuid -> original rpm_lights part (JSON) as it was before this plugin touched it.</summary>
        public Dictionary<string, string> Originals = new Dictionary<string, string>();

        /// <summary>Show each car's dash on the wheel's screen (see DashSwitcher).</summary>
        public bool DashSwitching = true;
        /// <summary>Remember the dash picked with the wheel's dash button while driving a car.</summary>
        public bool LearnDashes = true;
        /// <summary>Dash per car, keyed by "Game | CarId".</summary>
        public Dictionary<string, CarDash> CarDashes = new Dictionary<string, CarDash>();
        /// <summary>preset_uuid -> original screens part (the dash rotation) as it was before this plugin touched it.</summary>
        public Dictionary<string, string> ScreensOriginals = new Dictionary<string, string>();

        /// <summary>Drive the wheel's dash values from SimHub instead of SimPro's own game telemetry.</summary>
        public FeedSettings Feed = new FeedSettings();
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
        private DashSwitcher dashes;
        private readonly Dictionary<string, CarLedProfile> profileCache = new Dictionary<string, CarLedProfile>();
        private readonly object sync = new object();
        internal object SyncRoot => sync;
        private long lastDataTicks; // last DataUpdate with a car, for "driving" checks on the worker

        // SimHub -> SimPro telemetry feed (SimGame source)
        private SimGameFeed feed;
        private readonly SimProTelemetry feedData = new SimProTelemetry();
        private readonly object feedLock = new object();
        private volatile bool feedOn;
        private DateTime nextSourceCheckUtc;
        public string FeedStatus { get; private set; } = "";
        /// <summary>The game SimPro is reading telemetry from (null = none).</summary>
        public string SimProSource { get; private set; }
        /// <summary>A copy of the last values sent, for the settings page's live readout.</summary>
        internal SimProTelemetry FeedSnapshot { get; } = new SimProTelemetry();

        // Demo: a simulated lap instead of SimHub's data (not saved; off after a restart).
        private DemoCar demoCar;
        private Timer demoTimer;
        private readonly System.Diagnostics.Stopwatch demoClock = new System.Diagnostics.Stopwatch();
        private double demoLast;
        public bool DemoOn { get; private set; }
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
        // Live per-gear curves (LiveGearCurves): the current car's rpm_lights per gear, pushed on gear changes.
        private Dictionary<string, JObject> gearParts;
        private string gearPresetUuid, pushedGear;
        private volatile bool gearLive;
        private volatile string wantedGear;
        private int gearPushes;
        private long gearPushMsTotal, gearPushMsMax;

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

        // The current car for dash switching (set on every car change, even before its RPM range is known).
        public string DashCarKey { get; private set; }
        private string dashCarGame, dashCarId, dashCarName;
        public string CurrentCarNameForDash => dashCarName;
        public string CurrentGameForDash => dashCarGame;
        public string WheelDash => dashes?.WheelDash;
        public string DashStatus => dashes?.Status ?? "";

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
            if (Settings.CarDashes == null) Settings.CarDashes = new Dictionary<string, CarDash>();
            if (Settings.ScreensOriginals == null) Settings.ScreensOriginals = new Dictionary<string, string>();
            if (Settings.Feed == null) Settings.Feed = new FeedSettings();
            if (Settings.Feed.Overrides == null) Settings.Feed.Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            simPro.BaseUrl = Settings.SimProUrl;
            dashes = new DashSwitcher(this, simPro);
            feed = new SimGameFeed(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "Common", "FXProRpmSync"));
            if (Settings.Feed.Enabled) SetFeedEnabled(true, save: false);
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
            // Map to a wheel button: keep the dash the wheel shows now for this car (learning never overwrites it).
            this.AddAction("KeepWheelDashForCurrentCar", (a, b) => KeepWheelDashForCurrentCar());
            this.AttachDelegate("WheelDash", () => DashCatalog.NameOf(WheelDash));
            this.AttachDelegate("CurrentCarDash", () => DashCatalog.NameOf(GetCarDash(DashCarKey)?.DashId));

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
            if (feedOn) WriteFeed(pluginManager, data);
            if (!Settings.Enabled || !data.GameRunning || data.NewData == null) return;
            var d = data.NewData;

            double max = d.CarSettings_MaxRPM > 0 ? d.CarSettings_MaxRPM : d.MaxRpm;
            double red = d.CarSettings_RedLineRPM > 0 ? d.CarSettings_RedLineRPM : d.Redline;
            if (string.IsNullOrEmpty(d.CarId)) return;
            Interlocked.Exchange(ref lastDataTicks, DateTime.UtcNow.Ticks);

            var gear = GearKey(d.Gear);
            if (gear != wantedGear)
            {
                wantedGear = gear;
                if (gearLive) wake.Set();
            }

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

        /// <summary>SimHub's gear ("R", "N", "1".."10") as a SimPro/car data gear key.</summary>
        private static string GearKey(string gear)
        {
            if (string.IsNullOrEmpty(gear) || gear == "0") return "N";
            gear = gear.Trim().ToUpperInvariant();
            return gear == "R" || gear == "-1" ? "R" : gear;
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

        // ---------- Dash values from SimHub (SimGame feed) ----------

        /// <summary>Starts or stops feeding SimPro from SimHub. Stopping hands the wheel back to SimPro's game telemetry.</summary>
        public void SetFeedEnabled(bool on, bool save = true)
        {
            lock (feedLock)
            {
                try
                {
                    if (on)
                    {
                        feedData.Clear();
                        feed.Start(feedData.Buffer);
                        FeedStatus = "Running: SimPro reads SimHub's data while no game is selected in SimPro";
                    }
                    else
                    {
                        DemoOn = false;
                        demoTimer?.Dispose();
                        demoTimer = null;
                        feed.Stop();
                        FeedStatus = "Off: SimPro reads the game directly";
                    }
                    feedOn = on;
                }
                catch (Exception ex)
                {
                    feedOn = false;
                    try { feed.Stop(); } catch { }
                    FeedStatus = "Error: " + ex.Message;
                    SimHub.Logging.Current.Warn("[FXProRpmSync] SimGame feed: " + ex);
                }
            }
            Settings.Feed.Enabled = on;
            nextSourceCheckUtc = DateTime.MinValue;
            if (save) SaveSettings();
            SimHub.Logging.Current.Info("[FXProRpmSync] SimGame feed " + (feedOn ? "on" : "off"));
        }

        /// <summary>
        /// Demo mode: animates every dash value with a simulated lap (needs the feed, so it turns it on). SimPro must
        /// be reading SimGame, i.e. no game selected in SimPro.
        /// </summary>
        public void SetDemo(bool on)
        {
            if (on && !feedOn) SetFeedEnabled(true);
            lock (feedLock)
            {
                demoTimer?.Dispose();
                demoTimer = null;
                DemoOn = on && feedOn;
                if (DemoOn)
                {
                    demoCar = new DemoCar();
                    demoClock.Restart();
                    demoLast = 0;
                    demoTimer = new Timer(_ => DemoTick(), null, 0, 20);
                }
            }
            SimHub.Logging.Current.Info("[FXProRpmSync] demo " + (DemoOn ? "on" : "off"));
        }

        private void DemoTick()
        {
            lock (feedLock)
            {
                if (!DemoOn || !feedOn) return;
                double now = demoClock.Elapsed.TotalSeconds;
                try
                {
                    demoCar.Step(now - demoLast, feedData);
                    feed.Write(feedData.Buffer);
                    Buffer.BlockCopy(feedData.Buffer, 0, FeedSnapshot.Buffer, 0, SimProTelemetry.Size);
                }
                catch (Exception ex) { FeedStatus = "Error: " + ex.Message; }
                demoLast = now;
            }
        }

        private void WriteFeed(PluginManager pm, GameData data)
        {
            lock (feedLock)
            {
                if (!feedOn || DemoOn) return;
                try
                {
                    SimHubFeedMapper.Fill(feedData, data, pm, Settings.Feed, FeedError);
                    feed.Write(feedData.Buffer);
                    Buffer.BlockCopy(feedData.Buffer, 0, FeedSnapshot.Buffer, 0, SimProTelemetry.Size);
                }
                catch (Exception ex)
                {
                    FeedError("write", ex);
                }
            }
        }

        private readonly HashSet<string> loggedFeedErrors = new HashSet<string>();

        /// <summary>Shows the error and logs each distinct one once (the mapper runs every frame).</summary>
        private void FeedError(string section, Exception ex)
        {
            FeedStatus = $"Error in {section}: {ex.Message}";
            if (loggedFeedErrors.Add(section + "|" + ex.GetType().Name + "|" + ex.Message))
                SimHub.Logging.Current.Warn($"[FXProRpmSync] feed {section} failed: {ex}");
        }

        /// <summary>Which game SimPro reads (SimGame, or a real game that was already running), every 5 s.</summary>
        private async Task CheckFeedSource()
        {
            if (!feedOn || DateTime.UtcNow < nextSourceCheckUtc) return;
            nextSourceCheckUtc = DateTime.UtcNow.AddSeconds(5);
            try { SimProSource = await simPro.GetRunningGameName().ConfigureAwait(false); }
            catch { SimProSource = null; }
        }

        // ---------- Dash per car ----------

        public CarDash GetCarDash(string carKey)
        {
            lock (sync) return carKey != null && Settings.CarDashes.TryGetValue(carKey, out var d) ? d.Clone() : null;
        }

        public List<CarDash> AllCarDashes()
        {
            lock (sync) return Settings.CarDashes.Values.Select(d => d.Clone()).OrderBy(d => d.Game).ThenBy(d => d.CarName).ToList();
        }

        /// <param name="apply">Switch the wheel now if it's the current car (false when the wheel already shows it).</param>
        public void SaveCarDash(CarDash d, bool apply = true)
        {
            d.UpdatedUtc = DateTime.UtcNow;
            lock (sync) Settings.CarDashes[d.CarKey] = d.Clone();
            SaveSettings();
            if (apply && d.CarKey == DashCarKey) Reapply();
        }

        public void DeleteCarDash(string carKey)
        {
            bool removed;
            lock (sync) removed = Settings.CarDashes.Remove(carKey);
            if (!removed) return;
            SaveSettings();
            if (carKey == DashCarKey) Reapply();
        }

        /// <summary>A new entry for the current car (no dash set yet), or null when not in a car.</summary>
        public CarDash NewCarDashForCurrentCar() =>
            DashCarKey == null ? null : new CarDash { CarKey = DashCarKey, Game = dashCarGame, CarId = dashCarId, CarName = dashCarName };

        /// <summary>Picks a dash for the current car on the settings page (learning won't overwrite it).</summary>
        public void PickDashForCurrentCar(string dashId)
        {
            var d = GetCarDash(DashCarKey) ?? NewCarDashForCurrentCar();
            if (d == null || dashId == null) return;
            d.DashId = dashId;
            d.Learned = false;
            SaveCarDash(d);
        }

        public void KeepWheelDashForCurrentCar() => PickDashForCurrentCar(WheelDash);

        private bool Driving => DateTime.UtcNow.Ticks - Interlocked.Read(ref lastDataTicks) < TimeSpan.FromSeconds(2).Ticks;

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
            lock (sync) { Settings.Originals.Clear(); appliedFingerprints.Clear(); Settings.ScreensOriginals.Clear(); }
            dashes.ForgetOriginals();
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
            lock (feedLock) { DemoOn = false; demoTimer?.Dispose(); feedOn = false; feed?.Dispose(); }
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
                if (t == null && await PushGear().ConfigureAwait(false)) continue;
                if (t == null) t = await CheckGameMaxChanged().ConfigureAwait(false);
                if (t == null) { await PollDash().ConfigureAwait(false); await CheckFeedSource().ConfigureAwait(false); continue; }

                try
                {
                    if (t.Restore) await RestoreAsync().ConfigureAwait(false);
                    else
                    {
                        await ApplyDashAsync(t).ConfigureAwait(false);
                        await ApplyAsync(t).ConfigureAwait(false);
                    }
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

        private async Task ApplyDashAsync(Target t)
        {
            DashCarKey = t.CarKey;
            dashCarGame = t.GameName;
            dashCarId = t.CarId;
            dashCarName = string.IsNullOrEmpty(t.CarModel) ? t.CarId : t.CarModel;
            try
            {
                var w = await GetWheel().ConfigureAwait(false);
                await dashes.OnCarAsync(w, t.CarKey).ConfigureAwait(false);
            }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[FXProRpmSync] dash switch failed: " + ex.Message); }
        }

        private async Task PollDash()
        {
            if (wheel == null || !Settings.DashSwitching) return;
            try { await dashes.PollAsync(wheel, Driving, DashCarKey, NewCarDashForCurrentCar).ConfigureAwait(false); }
            catch (Exception ex) { SimHub.Logging.Current.Debug("[FXProRpmSync] dash poll failed: " + ex.Message); }
        }

        /// <summary>Live per-gear curves: sends the current gear's lights when the gear changed. True if it pushed.</summary>
        private async Task<bool> PushGear()
        {
            var gear = wantedGear;
            if (!gearLive || gearParts == null || gear == null || gear == pushedGear || wheel == null) return false;
            if (!gearParts.TryGetValue(gear, out var part)) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await simPro.SetPart(wheel, gearPresetUuid, RpmPart, RpmPartId, part).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[FXProRpmSync] gear push failed: " + ex.Message);
                pushedGear = null;
                gearLive = false; // the next car apply rebuilds it
                lock (sync) lastRequested = null;
                return false;
            }
            pushedGear = gear;
            long ms = sw.ElapsedMilliseconds;
            gearPushes++;
            gearPushMsTotal += ms;
            gearPushMsMax = Math.Max(gearPushMsMax, ms);
            if (gearPushes == 1 || gearPushes % 50 == 0)
                SimHub.Logging.Current.Info($"[FXProRpmSync] gear pushes: {gearPushes}, avg {gearPushMsTotal / gearPushes} ms, max {gearPushMsMax} ms (last: gear {gear}, {ms} ms)");
            return true;
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
                layout = RpmLayout.FromProfile(profile, includeGears: !w.OldDevice || Settings.LiveGearCurves);
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
            JObject mapped;
            if (w.OldDevice && Settings.LiveGearCurves && layout.Gears != null)
            {
                // The wheel can't switch curves by gear, so we do: one part per gear, the current one sent now.
                var parts = layout.Gears.Keys.ToDictionary(g => g, g => RpmLightsMapper.ToSimPro(original, layout.ForGear(g), scaleMax, false));
                var gear = wantedGear ?? "N";
                mapped = parts.TryGetValue(gear, out var p) ? p : RpmLightsMapper.ToSimPro(original, layout, scaleMax, false);
                foreach (var part in parts.Values) RememberApplied(presetUuid, part);
                gearParts = parts;
                gearPresetUuid = presetUuid;
                pushedGear = gear;
                gearLive = true;
                LightsSource += ", switched live by gear";
            }
            else
            {
                gearLive = false;
                gearParts = null;
                mapped = RpmLightsMapper.ToSimPro(original, layout, scaleMax, allowPerGear: !w.OldDevice);
            }

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
                var fp = RpmLightsMapper.Fingerprint(part);
                list.Remove(fp);
                list.Add(fp);
                if (list.Count > 40) list.RemoveAt(0); // room for a car's per-gear parts (12) plus history

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
            if (dashes != null && dashes.HasChanges)
            {
                try { await dashes.RestoreAsync(await GetWheel().ConfigureAwait(false)).ConfigureAwait(false); }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[FXProRpmSync] dash restore failed: " + ex.Message); }
            }

            gearLive = false;
            gearParts = null;
            pushedGear = null;
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
