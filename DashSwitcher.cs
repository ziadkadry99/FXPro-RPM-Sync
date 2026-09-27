using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace User.FXProRpmSync
{
    /// <summary>The dash chosen for one car: learned from the wheel's dash button or picked on the settings page.</summary>
    public class CarDash
    {
        public string CarKey;
        public string Game;
        public string CarId;
        public string CarName;
        public string DashId;
        /// <summary>Learned from the dash button. Picked ones (false) are never overwritten by learning.</summary>
        public bool Learned;
        public DateTime UpdatedUtc;

        public CarDash Clone() => (CarDash)MemberwiseClone();
    }

    /// <summary>
    /// Switches the wheel's dash per car through the preset's "screens" part (the dash rotation). The FX Pro ignores
    /// active_dash and always shows the first dash of the list it receives (verified on the wheel), so the preset's
    /// rotation is sent rotated to start at the car's dash; the dash button keeps cycling the same dashes.
    /// Runs on the plugin's worker thread only.
    /// </summary>
    internal class DashSwitcher
    {
        private const string ScreensPart = "screens";
        private const int ScreensPartId = 1;
        private const int MaxDashes = 10; // SimPro's UI allows up to 10 dashes in a rotation

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
        // After a push the wheel changes dash on its own; don't read that as the driver pressing the button.
        private static readonly TimeSpan QuietAfterPush = TimeSpan.FromSeconds(4);

        private readonly FXProRpmSyncPlugin plugin;
        private readonly SimProClient simPro;

        private string modifiedPresetUuid;
        private string handledCar, handledDash; // the car change / dash last acted on
        private string expectedDash;
        private DateTime quietUntilUtc, nextPollUtc;
        private DateTime? recheckAtUtc;
        private string lastSeenDash;
        // Recent screens parts we pushed, per preset: SimPro's read-back lags writes, so a read can return any of these.
        private readonly Dictionary<string, List<string>> pushed = new Dictionary<string, List<string>>();

        public string WheelDash { get; private set; }
        public string Status { get; private set; } = "";
        /// <summary>The wheel has a rotation from us that RestoreAsync would undo.</summary>
        public bool HasChanges => modifiedPresetUuid != null;

        public DashSwitcher(FXProRpmSyncPlugin plugin, SimProClient simPro)
        {
            this.plugin = plugin;
            this.simPro = simPro;
        }

        private FXProRpmSyncSettings Settings => plugin.Settings;

        /// <summary>A car change: show its dash; a car without one leaves the wheel as it is.</summary>
        /// <param name="force">Switch even if this car's dash was already handled (the re-check after a push).</param>
        public async Task OnCarAsync(SimProClient.Wheel w, string carKey, bool force = false)
        {
            if (!Settings.DashSwitching) { await RestoreAsync(w).ConfigureAwait(false); return; }

            var sel = await simPro.GetSelectedPreset(w).ConfigureAwait(false);
            var presetUuid = (string)sel["preset_uuid"];
            if (!(sel.SelectToken("config.screens.1") is JObject current)) return; // wheel without a screen
            if (modifiedPresetUuid != null && modifiedPresetUuid != presetUuid)
            {
                // The preset was switched in SimPro, which sent that preset's own rotation: SimPro is in charge of
                // the wheel now, so there's nothing of ours left to restore.
                modifiedPresetUuid = null;
            }

            var original = GetOrCaptureOriginal(presetUuid, current);
            var mapping = plugin.GetCarDash(carKey);

            if (mapping == null)
            {
                // Leave the wheel on whatever dash it's showing (sending anything would jump to the list's first dash).
                handledCar = carKey;
                handledDash = null;
                Status = "No dash saved for this car: leaving the wheel as it is";
                return;
            }
            // Only a car change or a new dash for this car switches the wheel; other re-applies (settings saved,
            // SimPro's max RPM arriving late) must not undo a dash picked with the button since.
            if (carKey == handledCar && mapping.DashId == handledDash && !force) return;
            handledCar = carKey;
            handledDash = mapping.DashId;

            // Each push rewrites a flash page on the wheel, so skip it when the wheel already shows the car's dash.
            var onWheel = await simPro.GetWheelDash(w).ConfigureAwait(false);
            if (onWheel == mapping.DashId)
            {
                Status = $"Showing {DashCatalog.NameOf(mapping.DashId)}";
                return;
            }
            var config = Rotate(original, mapping.DashId);
            await Push(w, presetUuid, config).ConfigureAwait(false);
            modifiedPresetUuid = presetUuid;
            recheckAtUtc = DateTime.UtcNow + QuietAfterPush;
            Status = $"Switched to {DashCatalog.NameOf(mapping.DashId)} ({(mapping.Learned ? "learned" : "picked")} for this car)";
            SimHub.Logging.Current.Info("[FXProRpmSync] " + Status);
        }

        /// <summary>
        /// Called about once a second by the worker. Reads the wheel's current dash; while driving, a change that
        /// wasn't ours is the driver pressing the dash button, and is remembered for the car.
        /// </summary>
        public async Task PollAsync(SimProClient.Wheel w, bool driving, string carKey, Func<CarDash> newForCurrentCar)
        {
            var now = DateTime.UtcNow;
            if (now < nextPollUtc) return;
            nextPollUtc = now + PollInterval;

            var dash = await simPro.GetWheelDash(w).ConfigureAwait(false);
            WheelDash = dash;
            if (dash == null) return;

            // A preset switch in SimPro (e.g. its per-game auto switch) right after a car change sends the preset's
            // own rotation and can land after ours: put the car's dash back once.
            if (recheckAtUtc.HasValue && now >= recheckAtUtc.Value)
            {
                recheckAtUtc = null;
                if (expectedDash != null && dash != expectedDash && carKey != null)
                {
                    SimHub.Logging.Current.Info($"[FXProRpmSync] wheel shows dash {dash} instead of {expectedDash}; re-sending");
                    await OnCarAsync(w, carKey, force: true).ConfigureAwait(false);
                    lastSeenDash = null;
                    return;
                }
            }

            if (now < quietUntilUtc || lastSeenDash == null)
            {
                lastSeenDash = dash;
                return;
            }
            if (dash != lastSeenDash && driving && Settings.DashSwitching && Settings.LearnDashes && carKey != null
                && dash != DashCatalog.SettingsPageId)
            {
                var existing = plugin.GetCarDash(carKey);
                if (existing == null || existing.Learned)
                {
                    var d = existing ?? newForCurrentCar();
                    if (d != null)
                    {
                        d.DashId = dash;
                        d.Learned = true;
                        plugin.SaveCarDash(d, apply: false); // the wheel is already showing it
                        Status = $"Learned {DashCatalog.NameOf(dash)} for this car";
                        SimHub.Logging.Current.Info("[FXProRpmSync] " + Status + ": " + carKey);
                    }
                }
            }
            lastSeenDash = dash;
        }

        /// <summary>Puts the preset's original rotation back if we changed it.</summary>
        public async Task RestoreAsync(SimProClient.Wheel w)
        {
            handledCar = handledDash = null; // switching back on applies the car's dash again
            var presetUuid = modifiedPresetUuid;
            if (presetUuid == null) return;
            string saved;
            lock (plugin.SyncRoot) Settings.ScreensOriginals.TryGetValue(presetUuid, out saved);
            if (saved != null) await Push(w, presetUuid, JObject.Parse(saved)).ConfigureAwait(false);
            modifiedPresetUuid = null;
            expectedDash = null;
            recheckAtUtc = null;
            Status = "Restored the preset's dash rotation";
            SimHub.Logging.Current.Info("[FXProRpmSync] " + Status);
        }

        /// <summary>Another wheel was attached: nothing of ours is on it, start over.</summary>
        public void WheelChanged()
        {
            modifiedPresetUuid = null;
            handledCar = handledDash = expectedDash = lastSeenDash = null;
            recheckAtUtc = null;
            WheelDash = null;
        }

        public void ForgetOriginals()
        {
            lock (plugin.SyncRoot) pushed.Clear();
        }

        private async Task Push(SimProClient.Wheel w, string presetUuid, JObject config)
        {
            await simPro.SetPart(w, presetUuid, ScreensPart, ScreensPartId, config).ConfigureAwait(false);
            var fp = Fingerprint(config);
            lock (plugin.SyncRoot)
            {
                if (!pushed.TryGetValue(presetUuid, out var list)) pushed[presetUuid] = list = new List<string>();
                list.Add(fp);
                if (list.Count > 20) list.RemoveAt(0);
            }
            expectedDash = Dashes(config).FirstOrDefault();
            quietUntilUtc = DateTime.UtcNow + QuietAfterPush;
            lastSeenDash = null;
        }

        /// <summary>The preset's rotation starting at dashId (added in front if it isn't in the rotation).</summary>
        internal static JObject Rotate(JObject original, string dashId)
        {
            var list = Dashes(original);
            int i = list.IndexOf(dashId);
            var rotated = i >= 0
                ? list.Skip(i).Concat(list.Take(i)).ToList()
                : new[] { dashId }.Concat(list).Take(MaxDashes).ToList();
            var config = (JObject)original.DeepClone();
            config["selected_dashs"] = new JArray(rotated);
            config["active_dash"] = dashId;
            return config;
        }

        private static List<string> Dashes(JObject screens) =>
            (screens?["selected_dashs"] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrEmpty(s)).ToList()
            ?? new List<string>();

        private static string Fingerprint(JObject screens) =>
            string.Join(",", Dashes(screens)) + "|" + (string)screens?["active_dash"];

        private JObject GetOrCaptureOriginal(string presetUuid, JObject current)
        {
            lock (plugin.SyncRoot)
            {
                var fp = Fingerprint(current);
                Settings.ScreensOriginals.TryGetValue(presetUuid, out var saved);
                pushed.TryGetValue(presetUuid, out var ours);

                bool isOurs = ours != null && ours.Contains(fp);
                bool isOriginal = saved != null && Fingerprint(JObject.Parse(saved)) == fp;

                // First sight of this preset, or the rotation was edited in SimPro since: current is the new baseline.
                if (saved == null || (!isOurs && !isOriginal && ours != null))
                {
                    saved = current.ToString(Newtonsoft.Json.Formatting.None);
                    Settings.ScreensOriginals[presetUuid] = saved;
                    plugin.SaveSettings();
                    SimHub.Logging.Current.Info("[FXProRpmSync] captured original dash rotation for preset " + presetUuid + ": " + fp);
                }
                return JObject.Parse(saved);
            }
        }
    }
}
