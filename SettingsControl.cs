using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace User.FXProRpmSync
{
    /// <summary>Settings page, built in code to keep the project XAML-free.</summary>
    public class SettingsControl : UserControl
    {
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x3d, 0x8b, 0xfd));
        private static readonly Brush CardBackground = Frozen(Color.FromArgb(0x14, 0xff, 0xff, 0xff));
        private static readonly Brush CardBorder = Frozen(Color.FromArgb(0x26, 0xff, 0xff, 0xff));

        private readonly FXProRpmSyncPlugin plugin;
        private FallbackStyle Fb => plugin.Settings.Fallback;

        private readonly List<(PatternKind Kind, Border Card, LedStrip Strip)> cards = new List<(PatternKind, Border, LedStrip)>();
        private readonly List<LedStrip> animated = new List<LedStrip>();
        private LedStrip bigStrip;
        private TextBlock bigRpm, bigTitle, presetNote, flashNote, startLabel;
        private StackPanel customize, customColors;
        private ComboBox schemeBox, color1, color2, color3, flashBox, flashColor;
        private FrameworkElement color2Row, color3Row, flashColorRow;
        private Slider startSlider;
        private bool loading;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly DispatcherTimer applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public SettingsControl(FXProRpmSyncPlugin plugin)
        {
            this.plugin = plugin;

            var page = new StackPanel { Margin = new Thickness(16), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
            page.Children.Add(BuildGeneral());
            page.Children.Add(new DashSection(plugin));
            page.Children.Add(new FeedSection(plugin));
            page.Children.Add(new OverridesSection(plugin));
            page.Children.Add(BuildFallback());
            page.Children.Add(Muted(
                "Car rev light data: Lovely Car Data by Lovely Sim Racing and contributors " +
                "(github.com/Lovely-Sim-Racing/lovely-car-data), licensed CC BY-NC-SA 4.0.", new Thickness(0, 20, 0, 0)));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };

            frameTimer.Tick += (s, e) => RenderFrame();
            applyTimer.Tick += (s, e) => { applyTimer.Stop(); plugin.SaveSettings(); plugin.Reapply(); };
            Loaded += (s, e) => { frameTimer.Start(); statusTimer.Start(); };
            Unloaded += (s, e) => { frameTimer.Stop(); statusTimer.Stop(); if (applyTimer.IsEnabled) { applyTimer.Stop(); plugin.SaveSettings(); plugin.Reapply(); } };

            LoadStyleIntoControls();
            Refresh();
        }

        // ---------- General ----------

        private UIElement BuildGeneral()
        {
            var panel = Section("FXPro RPM Sync",
                "Keeps your wheel's rev lights matched to the car you're driving. Cars in the rev light database get " +
                "their real pattern, colors and shift point; other cars use the style you pick below.");

            var enabled = new CheckBox { Content = "Enabled", IsChecked = plugin.Settings.Enabled, Margin = new Thickness(0, 0, 0, 8) };
            enabled.Checked += (s, e) => { plugin.Settings.Enabled = true; plugin.SaveSettings(); plugin.Reapply(); };
            enabled.Unchecked += (s, e) => { plugin.Settings.Enabled = false; plugin.SaveSettings(); plugin.RequestRestore(); };
            panel.Children.Add(enabled);

            var useDb = new CheckBox
            {
                Content = "Use each car's real rev lights when the car is in the database (untick to use your style below for every car)",
                IsChecked = plugin.Settings.UseCarDatabase,
                Margin = new Thickness(0, 0, 0, 12),
            };
            useDb.Checked += (s, e) => { plugin.Settings.UseCarDatabase = true; plugin.SaveSettings(); plugin.Reapply(); };
            useDb.Unchecked += (s, e) => { plugin.Settings.UseCarDatabase = false; plugin.SaveSettings(); plugin.Reapply(); };
            panel.Children.Add(useDb);

            var liveGears = new CheckBox
            {
                Content = "Switch the lights by gear for cars whose real lights differ per gear (sends the gear's lights to SimPro on every gear change)",
                IsChecked = plugin.Settings.LiveGearCurves,
                Margin = new Thickness(0, -6, 0, 12),
            };
            liveGears.Checked += (s, e) => { plugin.Settings.LiveGearCurves = true; plugin.SaveSettings(); plugin.Reapply(); };
            liveGears.Unchecked += (s, e) => { plugin.Settings.LiveGearCurves = false; plugin.SaveSettings(); plugin.Reapply(); };
            panel.Children.Add(liveGears);

            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Opacity = 0.85 };
            panel.Children.Add(new Border
            {
                Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = status,
            });
            statusTimer.Tick += (s, e) => status.Text =
                $"Status   {plugin.Status}\nCar      {plugin.CurrentCar}\nLights   {plugin.LightsSource}\n" +
                $"Shift    {plugin.AppliedRedline:0} rpm    Game max {plugin.AppliedMaxRpm:0} rpm";

            var buttons = new WrapPanel();
            buttons.Children.Add(MakeButton("Restore original preset lights", () => plugin.RequestRestore()));
            buttons.Children.Add(MakeButton("Re-capture preset (after editing it in SimPro)", () => { plugin.RequestRestore(); plugin.ForgetOriginals(); }));
            panel.Children.Add(buttons);
            return panel;
        }

        // ---------- Fallback style ----------

        private UIElement BuildFallback()
        {
            var panel = Section("Cars without rev light data",
                "Pick how the lights look for cars that aren't in the database. Previews show a rev sweep up to the " +
                "shift point, which is the car's redline in SimHub's Car Settings.");

            var grid = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
            foreach (var entry in LedPatterns.Catalog)
            {
                var strip = new LedStrip(9);
                animated.Add(strip);
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
                body.Children.Add(strip);
                body.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });

                var card = new Border
                {
                    Width = 250, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(8), Background = CardBackground, BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand, Child = body, ToolTip = entry.Description,
                };
                var kind = entry.Kind;
                card.MouseLeftButtonUp += (s, e) => { Fb.Pattern = kind; StyleChanged(); };
                cards.Add((kind, card, strip));
                grid.Children.Add(card);
            }
            panel.Children.Add(grid);

            // Large live preview of the current choice.
            bigTitle = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            bigStrip = new LedStrip(20);
            bigRpm = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Consolas") };
            var previewBody = new StackPanel();
            previewBody.Children.Add(bigTitle);
            previewBody.Children.Add(bigStrip);
            previewBody.Children.Add(bigRpm);
            panel.Children.Add(new Border
            {
                Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12),
                MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Child = previewBody,
            });

            presetNote = Muted("This uses the LED pattern and colors of the preset selected in SimPro Manager. To change it, " +
                               "edit the preset's RPM lights in SimPro, save, then press \"Re-capture preset\" above.", new Thickness(0, 0, 0, 8));
            panel.Children.Add(presetNote);

            customize = new StackPanel();
            panel.Children.Add(customize);

            // Colors
            schemeBox = new ComboBox { Width = 220 };
            foreach (var sc in LedPatterns.SchemeCatalog) schemeBox.Items.Add(new ComboBoxItem { Content = sc.Title, Tag = sc.Scheme });
            schemeBox.SelectionChanged += (s, e) => { if (!loading) { Fb.Colors = (ColorScheme)((ComboBoxItem)schemeBox.SelectedItem).Tag; StyleChanged(); } };
            customize.Children.Add(Row("Colors", schemeBox));

            color1 = ColorPicker(hex => Fb.Color1 = hex);
            color2 = ColorPicker(hex => Fb.Color2 = hex);
            color3 = ColorPicker(hex => Fb.Color3 = hex);
            customColors = new StackPanel();
            customColors.Children.Add(Row("First LEDs", color1));
            customColors.Children.Add(color2Row = Row("Middle LEDs", color2));
            customColors.Children.Add(color3Row = Row("Last LEDs", color3));
            customize.Children.Add(customColors);

            // Start point
            startSlider = new Slider { Minimum = 50, Maximum = 98, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 260, VerticalAlignment = VerticalAlignment.Center };
            startLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8 };
            startSlider.ValueChanged += (s, e) => { if (!loading) { Fb.StartPercent = startSlider.Value; StyleChanged(); } };
            var startRow = new StackPanel { Orientation = Orientation.Horizontal };
            startRow.Children.Add(startSlider);
            startRow.Children.Add(startLabel);
            customize.Children.Add(Row("First LED at", startRow));

            // Flash
            flashBox = new ComboBox { Width = 220 };
            flashBox.Items.Add(new ComboBoxItem { Content = "No flash", Tag = FlashMode.None });
            flashBox.Items.Add(new ComboBoxItem { Content = "Solid color at the shift point", Tag = FlashMode.Solid });
            flashBox.Items.Add(new ComboBoxItem { Content = "Blink at the shift point", Tag = FlashMode.Blink });
            flashBox.SelectionChanged += (s, e) => { if (!loading) { Fb.Flash = (FlashMode)((ComboBoxItem)flashBox.SelectedItem).Tag; StyleChanged(); } };
            customize.Children.Add(Row("Shift flash", flashBox));
            flashColor = ColorPicker(hex => Fb.FlashColor = hex);
            customize.Children.Add(flashColorRow = Row("Flash color", flashColor));
            flashNote = Muted("Without real data the shift point is SimHub's redline for the car: 95% of its max RPM unless " +
                              "you set it in SimHub's Car Settings. The flash is a guess unless you set it there.", new Thickness(150, 2, 0, 0));
            customize.Children.Add(flashNote);

            return panel;
        }

        // ---------- State ----------

        private void LoadStyleIntoControls()
        {
            loading = true;
            schemeBox.SelectedItem = schemeBox.Items.Cast<ComboBoxItem>().First(i => (ColorScheme)i.Tag == Fb.Colors);
            flashBox.SelectedItem = flashBox.Items.Cast<ComboBoxItem>().First(i => (FlashMode)i.Tag == Fb.Flash);
            SelectColor(color1, Fb.Color1);
            SelectColor(color2, Fb.Color2);
            SelectColor(color3, Fb.Color3);
            SelectColor(flashColor, Fb.FlashColor);
            startSlider.Value = Fb.StartPercent;
            loading = false;
        }

        private void StyleChanged()
        {
            Refresh();
            applyTimer.Stop();
            applyTimer.Start(); // debounce: save + push to the wheel once the user stops fiddling
        }

        private void Refresh()
        {
            foreach (var (kind, card, strip) in cards)
            {
                strip.Layout = LayoutFor(kind, withFlash: false);
                card.BorderBrush = kind == Fb.Pattern ? Accent : CardBorder;
            }
            bool preset = Fb.Pattern == PatternKind.SimProPreset;
            bigStrip.Layout = LayoutFor(Fb.Pattern, withFlash: true);
            bigTitle.Text = "Preview: " + LedPatterns.Catalog.First(c => c.Kind == Fb.Pattern).Title;

            presetNote.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
            customize.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;
            customColors.Visibility = Fb.Colors == ColorScheme.Single || Fb.Colors == ColorScheme.Custom ? Visibility.Visible : Visibility.Collapsed;
            color2Row.Visibility = color3Row.Visibility = Fb.Colors == ColorScheme.Custom ? Visibility.Visible : Visibility.Collapsed;
            flashColorRow.Visibility = flashNote.Visibility = Fb.Flash == FlashMode.None ? Visibility.Collapsed : Visibility.Visible;
            startLabel.Text = $"{Fb.StartPercent:0}% of the shift point";
        }

        /// <summary>Card previews show each pattern with the current colors/start point, so they compare like for like.</summary>
        private LedLayout LayoutFor(PatternKind kind, bool withFlash)
        {
            if (kind == PatternKind.SimProPreset)
                return RpmLightsMapper.PresetLayout(plugin.PresetTemplate);
            var s = Fb.Clone();
            s.Pattern = kind;
            if (!withFlash) s.Flash = FlashMode.None;
            return LedPatterns.Build(s);
        }

        private void RenderFrame()
        {
            double t = clock.Elapsed.TotalSeconds;
            bool blinkOn = ((int)(t * 1000 / (RpmLightsMapper.DefaultBlinkUnits * RpmLightsMapper.BlinkMsPerUnit))) % 2 == 0;
            foreach (var strip in animated.Append(bigStrip))
            {
                var first = strip.Layout?.Fractions.Where(f => f > 0).DefaultIfEmpty(0.8).Min() ?? 0.8;
                double rpm = LedStrip.SweepRpm(t, first);
                strip.Render(rpm, blinkOn);
                if (strip == bigStrip)
                    bigRpm.Text = rpm >= 1.0 ? "At the shift point" : $"{rpm * 100:0}% of the shift point";
            }
        }

        // ---------- Small UI helpers ----------

        private static StackPanel Section(string title, string subtitle)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
            p.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            p.Children.Add(Muted(subtitle, new Thickness(0, 0, 0, 12)));
            return p;
        }

        private static TextBlock Muted(string text, Thickness margin) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = margin, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

        private static FrameworkElement Row(string label, UIElement control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            var l = new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            if (control is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(control);
            return row;
        }

        private static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private ComboBox ColorPicker(Action<string> set)
        {
            var box = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (name, hex) in LedPalette.All)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Ellipse
                {
                    Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0),
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                });
                content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                box.Items.Add(new ComboBoxItem { Content = content, Tag = hex });
            }
            box.SelectionChanged += (s, e) => { if (!loading && box.SelectedItem is ComboBoxItem i) { set((string)i.Tag); StyleChanged(); } };
            return box;
        }

        private static void SelectColor(ComboBox box, string hex) =>
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, hex, StringComparison.OrdinalIgnoreCase))
                               ?? box.Items[0];

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
