using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.FXProRpmSync
{
    /// <summary>"Dash values from SimHub" part of the settings page: on/off, status, options, live readout, overrides.</summary>
    public class FeedSection : StackPanel
    {
        private static readonly Brush CardBackground = Frozen(Color.FromArgb(0x14, 0xff, 0xff, 0xff));
        private static readonly Brush CardBorder = Frozen(Color.FromArgb(0x26, 0xff, 0xff, 0xff));

        /// <summary>Values SimHub only has in the raw game data, with what the wheel shows them as.</summary>
        private static readonly (string Field, string Label)[] RawFields =
        {
            ("tcCut", "TC2 / TC cut (tc2)"),
            ("ersMode", "ERS mode (ersm, 0-15)"),
            ("frontAntiRollBar", "Front ARB (farb)"),
            ("rearAntiRollBar", "Rear ARB (rarb)"),
            ("engineBraking", "Engine braking (eb)"),
            ("diffEntry", "Diff entry (entr)"),
            ("diffMiddle", "Diff mid (mid)"),
            ("diffExit", "Diff high speed (hspd)"),
            ("diffAdjOnThrottle", "Diff (diff)"),
            ("throttleShape", "Throttle shape (tps)"),
        };

        private readonly FXProRpmSyncPlugin plugin;
        private readonly TextBlock status, source, readout;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        public FeedSection(FXProRpmSyncPlugin plugin)
        {
            this.plugin = plugin;
            Margin = new Thickness(0, 0, 0, 24);
            var fs = plugin.Settings.Feed;

            Children.Add(new TextBlock { Text = "Dash values from SimHub", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            Children.Add(Muted(
                "Sends SimHub's data to the wheel's dash instead of SimPro's own game telemetry, so every value SimHub knows " +
                "(gaps, fuel per lap, tyre data, ...) reaches the dash in every game SimHub supports. It works through SimPro's " +
                "built-in \"SimGame\" source: SimPro picks its data source when a game starts and keeps it, so turn this on " +
                "(or start SimHub) before starting the game; restarting SimHub is fine. While it's on, SimPro sees SimGame instead of your game: its " +
                "per-game preset switching doesn't trigger, and anything SimPro drives from game telemetry (dash, rev lights, " +
                "telemetry effects) uses SimHub's data. Force feedback is unaffected.", new Thickness(0, 0, 0, 12)));

            var enabled = new CheckBox { Content = "Drive the wheel's dash from SimHub", IsChecked = fs.Enabled, Margin = new Thickness(0, 0, 0, 6) };
            var demo = new CheckBox
            {
                Content = "Demo: animate every dash value with a simulated lap (with no game running; off again after a restart)",
                IsChecked = plugin.DemoOn, Margin = new Thickness(0, 0, 0, 8),
            };
            enabled.Checked += (s, e) => plugin.SetFeedEnabled(true);
            enabled.Unchecked += (s, e) => { plugin.SetFeedEnabled(false); demo.IsChecked = false; };
            demo.Checked += (s, e) => { plugin.SetDemo(true); enabled.IsChecked = plugin.Settings.Feed.Enabled; };
            demo.Unchecked += (s, e) => plugin.SetDemo(false);
            Children.Add(enabled);
            Children.Add(demo);

            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
            source = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            var statusBody = new StackPanel();
            statusBody.Children.Add(status);
            statusBody.Children.Add(source);
            Children.Add(Card(statusBody, new Thickness(0, 0, 0, 12)));

            // Options
            var gaps = new ComboBox { Width = 380 };
            AddItem(gaps, "Auto: race = by position in my class, other sessions = on track", GapMode.Auto);
            AddItem(gaps, "By race position, in my class", GapMode.RaceClass);
            AddItem(gaps, "By race position, overall", GapMode.RaceOverall);
            AddItem(gaps, "On track (nearest car, any class or lap)", GapMode.OnTrack);
            Select(gaps, fs.Gaps);
            gaps.SelectionChanged += (s, e) => { fs.Gaps = (GapMode)((ComboBoxItem)gaps.SelectedItem).Tag; plugin.SaveSettings(); };
            Children.Add(Row("Gap ahead / behind", gaps));

            var delta = new ComboBox { Width = 380 };
            AddItem(delta, "Session best lap", DeltaSource.SessionBest);
            AddItem(delta, "All-time best lap", DeltaSource.AllTimeBest);
            Select(delta, fs.Delta);
            delta.SelectionChanged += (s, e) => { fs.Delta = (DeltaSource)((ComboBoxItem)delta.SelectedItem).Tag; plugin.SaveSettings(); };
            Children.Add(Row("Delta (gain / loss)", delta));

            // Live readout of what the wheel is being sent
            readout = new TextBlock { FontFamily = new FontFamily("Consolas"), Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var readoutBody = new StackPanel();
            readoutBody.Children.Add(Small("SENDING TO THE WHEEL (AS THE DASH SHOWS IT, IN SIMPRO'S BASE UNITS)"));
            readoutBody.Children.Add(readout);
            Children.Add(Card(readoutBody, new Thickness(0, 4, 0, 12)));

            // Game-specific values
            var raw = new StackPanel();
            raw.Children.Add(Muted(
                "These aren't in SimHub's standard data. Built in: ACC's TC cut, and iRacing's in-car adjustments (dc* values) " +
                "for cars that have them. To use another value, enter a SimHub property path (as in SimHub's property list, " +
                "e.g. DataCorePlugin.GameRawData.Telemetry.dcAntiRollFront). Leave empty for the built-in value.", new Thickness(0, 0, 0, 8)));
            foreach (var (field, label) in RawFields)
            {
                fs.Overrides.TryGetValue(field, out var path);
                var box = new TextBox { Width = 460, Text = path ?? "" };
                var f = field;
                box.LostFocus += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(box.Text)) fs.Overrides.Remove(f);
                    else fs.Overrides[f] = box.Text.Trim();
                    plugin.SaveSettings();
                };
                raw.Children.Add(Row(label, box));
            }
            Children.Add(new Expander { Header = "Game-specific values", Content = raw, Margin = new Thickness(0, 0, 0, 4) });

            timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { timer.Start(); Refresh(); };
            Unloaded += (s, e) => timer.Stop();
        }

        private void Refresh()
        {
            status.Text = plugin.FeedStatus;
            bool on = plugin.Settings.Feed.Enabled;
            var src = plugin.SimProSource;
            if (!on)
            {
                source.Text = "";
                readout.Text = "(off)";
                return;
            }
            source.Text = src == null
                ? "SimPro lists no game, which is normal while it reads SimHub's data (SimPro never lists SimGame)."
                : "SimPro is reading " + src + " directly, not SimHub's data: it picked the game before SimGame was running. " +
                  "Close the game and start it again. If the dash still doesn't follow SimHub, restart SimPro " +
                  "(tray icon > Exit, then start it) and, if needed, the wheelbase.";

            var t = plugin.FeedSnapshot;
            if (t.Get("isGameRunning") == 0) { readout.Text = "No game running in SimHub (tick Demo to animate the dash)."; return; }
            string gear = t.Get("gear") < 0 ? "R" : t.Get("gear") == 0 ? "N" : t.Get("gear").ToString("0");
            readout.Text =
                $"gear {gear}   speed {t.Get("speed"):0} km/h   rpm {t.Get("rpm"):0} / {t.Get("maxRpm"):0}\n" +
                $"pos {t.Get("position"):0}   lap {t.Get("completedLaps"):0}   last {Lap(t.Get("lastLapTime"))}   best {Lap(t.Get("bestLapTime"))}   delta {t.Get("gainLoss"):+0.00;-0.00}\n" +
                $"gap ahead {t.Get("gapAhead") / 100:0.0}   gap behind -{t.Get("gapBehind") / 100:0.0}\n" +
                $"fuel {t.Get("fuel"):0.0} L   per lap {t.Get("fuelPerLap") / 10:0.0} L   bias {t.Get("brakeBias"):0.0}%   ABS {t.Get("absLevel"):0}  TC {t.Get("tcLevel"):0}/{t.Get("tcCut"):0}  map {t.Get("engineMap"):0}\n" +
                $"tyres °C   {t.Get("tyreTemperature0"):0} {t.Get("tyreTemperature1"):0} {t.Get("tyreTemperature2"):0} {t.Get("tyreTemperature3"):0}" +
                $"   psi {t.Get("tyrePressure0"):0.0} {t.Get("tyrePressure1"):0.0} {t.Get("tyrePressure2"):0.0} {t.Get("tyrePressure3"):0.0}" +
                $"   wear % {t.Get("tyreWear0"):0} {t.Get("tyreWear1"):0} {t.Get("tyreWear2"):0} {t.Get("tyreWear3"):0}\n" +
                $"brakes °C {t.Get("brakeTemperature0"):0} {t.Get("brakeTemperature1"):0} {t.Get("brakeTemperature2"):0} {t.Get("brakeTemperature3"):0}" +
                $"   water {t.Get("waterTemperature"):0} °C   oil {t.Get("oilTemperature"):0} °C";
        }

        private static string Lap(double ms)
        {
            if (ms <= 0) return "-";
            var ts = TimeSpan.FromMilliseconds(ms);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        private static void AddItem(ComboBox box, string text, object tag) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

        private static void Select(ComboBox box, object tag) =>
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? box.Items[0];

        private static Border Card(UIElement child, Thickness margin) => new Border
        {
            Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = margin, Child = child, MaxWidth = 1060,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        private static TextBlock Muted(string text, Thickness margin) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = margin, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

        private static TextBlock Small(string text) =>
            new TextBlock { Text = text, FontSize = 10, Opacity = 0.55, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

        private static FrameworkElement Row(string label, UIElement control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var l = new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            if (control is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(control);
            return row;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
