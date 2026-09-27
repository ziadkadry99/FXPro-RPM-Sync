using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace User.FXProRpmSync
{
    /// <summary>"Per-car overrides" part of the settings page: current car, override editor, saved overrides list.</summary>
    public class OverridesSection : StackPanel
    {
        private static readonly Brush CardBackground = Frozen(Color.FromArgb(0x14, 0xff, 0xff, 0xff));
        private static readonly Brush CardBorder = Frozen(Color.FromArgb(0x26, 0xff, 0xff, 0xff));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x3d, 0x8b, 0xfd));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xe8, 0x47, 0x49));

        private readonly FXProRpmSyncPlugin plugin;

        // Current car panel
        private readonly TextBlock carTitle, carInfo;
        private readonly WrapPanel carButtons;
        private string shownCarKey, shownSummary;

        // Editor
        private readonly Border editorCard;
        private readonly TextBlock editorTitle, offsetInfo, previewRpm;
        private readonly RadioButton kindOffset, kindCustom, kindPattern;
        private readonly StackPanel offsetPanel, customPanel, patternPanel, patternCustomize;
        private readonly ComboBox patternBox, schemeBox, pColor1, pColor2, pColor3, pFlashBox, pFlashColor;
        private readonly FrameworkElement pColor1Row, pColor2Row, pColor3Row, pFlashColorRow;
        private readonly Slider pStart;
        private readonly TextBlock pStartLabel, patternInfo;
        private readonly Slider offsetSlider;
        private readonly TextBox offsetBox;
        private readonly TextBox[] ledRpm = new TextBox[RpmLightsMapper.WheelLeds];
        private readonly ComboBox[] ledColor = new ComboBox[RpmLightsMapper.WheelLeds];
        private readonly TextBox flashRpmBox;
        private readonly ComboBox flashModeBox, flashColorBox;
        private readonly Button resetButton, deleteButton;
        private readonly LedStrip preview;
        private CarOverride editing;
        private RpmLayout editBase;
        private bool loading;

        // List
        private readonly StackPanel list;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly DispatcherTimer carTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public OverridesSection(FXProRpmSyncPlugin plugin)
        {
            this.plugin = plugin;
            Margin = new Thickness(0, 0, 0, 24);

            Children.Add(new TextBlock { Text = "Per-car overrides", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            Children.Add(Muted("Fine-tune a car when its lights don't match the game: shift the whole sequence earlier or later, or set " +
                               "every LED yourself. Overrides are saved per car and applied automatically. You can also map the " +
                               "SimHub actions \"CurrentCarLightsLater\" / \"CurrentCarLightsEarlier\" to wheel buttons to nudge " +
                               $"the current car by {FXProRpmSyncPlugin.NudgeStepRpm} rpm while driving.", new Thickness(0, 0, 0, 12)));

            // ----- Current car -----
            carTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
            carInfo = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
            carButtons = new WrapPanel();
            var carBody = new StackPanel();
            carBody.Children.Add(Small("CURRENT CAR"));
            carBody.Children.Add(carTitle);
            carBody.Children.Add(carInfo);
            carBody.Children.Add(carButtons);
            Children.Add(Card(carBody, new Thickness(0, 0, 0, 12)));

            // ----- Editor -----
            editorTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            kindOffset = new RadioButton { Content = "Shift the whole sequence", GroupName = "ovkind", Margin = new Thickness(0, 0, 20, 0) };
            kindCustom = new RadioButton { Content = "Set each LED", GroupName = "ovkind", Margin = new Thickness(0, 0, 20, 0) };
            kindPattern = new RadioButton { Content = "Use a pattern", GroupName = "ovkind" };
            kindOffset.Checked += (s, e) => SetKind(OverrideKind.Offset);
            kindCustom.Checked += (s, e) => SetKind(OverrideKind.Custom);
            kindPattern.Checked += (s, e) => SetKind(OverrideKind.Pattern);
            var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            kindRow.Children.Add(kindOffset);
            kindRow.Children.Add(kindCustom);
            kindRow.Children.Add(kindPattern);

            // Offset
            offsetSlider = new Slider { Minimum = -1500, Maximum = 1500, TickFrequency = 10, IsSnapToTickEnabled = true, Width = 320, VerticalAlignment = VerticalAlignment.Center };
            offsetSlider.ValueChanged += (s, e) => { if (!loading) SetOffset((int)offsetSlider.Value); };
            offsetBox = new TextBox { Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            offsetBox.TextChanged += (s, e) => { if (!loading && int.TryParse(offsetBox.Text.Replace("+", ""), out var v)) SetOffset(v, fromBox: true); };
            var offsetRow = new StackPanel { Orientation = Orientation.Horizontal };
            offsetRow.Children.Add(MakeButton($"-{FXProRpmSyncPlugin.NudgeStepRpm}", () => SetOffset(editing.OffsetRpm - FXProRpmSyncPlugin.NudgeStepRpm), 0));
            offsetRow.Children.Add(offsetSlider);
            offsetRow.Children.Add(MakeButton($"+{FXProRpmSyncPlugin.NudgeStepRpm}", () => SetOffset(editing.OffsetRpm + FXProRpmSyncPlugin.NudgeStepRpm), 8));
            offsetRow.Children.Add(offsetBox);
            offsetRow.Children.Add(new TextBlock { Text = "rpm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.75 });
            offsetInfo = new TextBlock { Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0) };
            offsetPanel = new StackPanel();
            offsetPanel.Children.Add(offsetRow);
            offsetPanel.Children.Add(offsetInfo);

            // Custom: one column per LED
            var ledGrid = new Grid { Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            ledGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            for (int i = 0; i < RpmLightsMapper.WheelLeds; i++) ledGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            for (int r = 0; r < 3; r++) ledGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddToGrid(ledGrid, Small("LED"), 0, 0);
            AddToGrid(ledGrid, Small("RPM"), 1, 0);
            AddToGrid(ledGrid, Small("COLOR"), 2, 0);
            for (int i = 0; i < RpmLightsMapper.WheelLeds; i++)
            {
                int idx = i;
                AddToGrid(ledGrid, new TextBlock { Text = (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 }, 0, i + 1);
                ledRpm[i] = new TextBox { Margin = new Thickness(2), TextAlignment = TextAlignment.Right, ToolTip = "RPM where this LED lights. 0 = never (unused)." };
                ledRpm[i].TextChanged += (s, e) => { if (!loading) SetLedRpm(idx); };
                AddToGrid(ledGrid, ledRpm[i], 1, i + 1);
                ledColor[i] = ColorPicker(compact: true, includeOff: true);
                ledColor[i].SelectionChanged += (s, e) => { if (!loading && ledColor[idx].SelectedItem is ComboBoxItem it) { editing.Custom.Colors[idx] = (string)it.Tag; Changed(); } };
                AddToGrid(ledGrid, ledColor[i], 2, i + 1);
            }

            flashRpmBox = new TextBox { Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            flashRpmBox.TextChanged += (s, e) =>
            {
                if (loading) return;
                bool ok = int.TryParse(flashRpmBox.Text, out var v) && v >= 0 && v <= 20000;
                flashRpmBox.BorderBrush = ok ? null : ErrorBrush;
                if (ok) { editing.Custom.FlashRpm = v; Changed(); }
            };
            flashModeBox = new ComboBox { Width = 120, Margin = new Thickness(10, 0, 0, 0) };
            flashModeBox.Items.Add(new ComboBoxItem { Content = "No flash", Tag = -1 });
            flashModeBox.Items.Add(new ComboBoxItem { Content = "Solid", Tag = 0 });
            flashModeBox.Items.Add(new ComboBoxItem { Content = "Blink", Tag = RpmLightsMapper.DefaultBlinkUnits });
            flashModeBox.SelectionChanged += (s, e) =>
            {
                if (loading || !(flashModeBox.SelectedItem is ComboBoxItem it)) return;
                int tag = (int)it.Tag;
                if (tag < 0) editing.Custom.FlashRpm = 0;
                else
                {
                    if (editing.Custom.FlashRpm <= 0) editing.Custom.FlashRpm = Math.Max(1, editing.Custom.Rpm.DefaultIfEmpty(0).Max());
                    editing.Custom.FlashBlinkUnits = tag;
                }
                LoadCustom();
                Changed();
            };
            flashColorBox = ColorPicker(compact: false, includeOff: false);
            flashColorBox.Margin = new Thickness(10, 0, 0, 0);
            flashColorBox.SelectionChanged += (s, e) => { if (!loading && flashColorBox.SelectedItem is ComboBoxItem it) { editing.Custom.FlashColor = (string)it.Tag; Changed(); } };
            var flashRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            flashRow.Children.Add(new TextBlock { Text = "Shift flash at", Width = 100, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
            flashRow.Children.Add(flashRpmBox);
            flashRow.Children.Add(new TextBlock { Text = "rpm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.75 });
            flashRow.Children.Add(flashModeBox);
            flashRow.Children.Add(flashColorBox);

            var customTools = new WrapPanel();
            customTools.Children.Add(MakeButton($"All {FXProRpmSyncPlugin.NudgeStepRpm} rpm earlier", () => { editing.Custom = editing.Custom.Offset(-FXProRpmSyncPlugin.NudgeStepRpm); LoadCustom(); Changed(); }, 8));
            customTools.Children.Add(MakeButton($"All {FXProRpmSyncPlugin.NudgeStepRpm} rpm later", () => { editing.Custom = editing.Custom.Offset(FXProRpmSyncPlugin.NudgeStepRpm); LoadCustom(); Changed(); }, 8));
            resetButton = MakeButton("Reset to the car's lights", () => { editing.Custom = editBase.Clone(); LoadCustom(); Changed(); }, 8);
            customTools.Children.Add(resetButton);

            customPanel = new StackPanel();
            customPanel.Children.Add(ledGrid);
            customPanel.Children.Add(flashRow);
            customPanel.Children.Add(customTools);

            // Pattern: one of the fallback patterns for this car only, at the car's own shift point.
            patternBox = new ComboBox { Width = 240 };
            foreach (var c in LedPatterns.Catalog) patternBox.Items.Add(new ComboBoxItem { Content = c.Title, Tag = c.Kind, ToolTip = c.Description });
            patternBox.SelectionChanged += (s, e) => { if (!loading && patternBox.SelectedItem is ComboBoxItem it) { editing.Style.Pattern = (PatternKind)it.Tag; LoadPattern(); Changed(); } };
            schemeBox = new ComboBox { Width = 240 };
            foreach (var sc in LedPatterns.SchemeCatalog) schemeBox.Items.Add(new ComboBoxItem { Content = sc.Title, Tag = sc.Scheme });
            schemeBox.SelectionChanged += (s, e) => { if (!loading && schemeBox.SelectedItem is ComboBoxItem it) { editing.Style.Colors = (ColorScheme)it.Tag; LoadPattern(); Changed(); } };
            pColor1 = PatternColor(hex => editing.Style.Color1 = hex);
            pColor2 = PatternColor(hex => editing.Style.Color2 = hex);
            pColor3 = PatternColor(hex => editing.Style.Color3 = hex);
            pStart = new Slider { Minimum = 50, Maximum = 98, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 240, VerticalAlignment = VerticalAlignment.Center };
            pStartLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8 };
            pStart.ValueChanged += (s, e) => { if (!loading) { editing.Style.StartPercent = pStart.Value; LoadPattern(); Changed(); } };
            var startRow = new StackPanel { Orientation = Orientation.Horizontal };
            startRow.Children.Add(pStart);
            startRow.Children.Add(pStartLabel);
            pFlashBox = new ComboBox { Width = 240 };
            pFlashBox.Items.Add(new ComboBoxItem { Content = "No flash", Tag = FlashMode.None });
            pFlashBox.Items.Add(new ComboBoxItem { Content = "Solid color at the shift point", Tag = FlashMode.Solid });
            pFlashBox.Items.Add(new ComboBoxItem { Content = "Blink at the shift point", Tag = FlashMode.Blink });
            pFlashBox.SelectionChanged += (s, e) => { if (!loading && pFlashBox.SelectedItem is ComboBoxItem it) { editing.Style.Flash = (FlashMode)it.Tag; LoadPattern(); Changed(); } };
            pFlashColor = PatternColor(hex => editing.Style.FlashColor = hex);

            patternCustomize = new StackPanel();
            patternCustomize.Children.Add(FormRow("Colors", schemeBox));
            patternCustomize.Children.Add(pColor1Row = FormRow("First LEDs", pColor1));
            patternCustomize.Children.Add(pColor2Row = FormRow("Middle LEDs", pColor2));
            patternCustomize.Children.Add(pColor3Row = FormRow("Last LEDs", pColor3));
            patternCustomize.Children.Add(FormRow("First LED at", startRow));
            patternCustomize.Children.Add(FormRow("Shift flash", pFlashBox));
            patternCustomize.Children.Add(pFlashColorRow = FormRow("Flash color", pFlashColor));
            patternInfo = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

            patternPanel = new StackPanel();
            patternPanel.Children.Add(FormRow("Pattern", patternBox));
            patternPanel.Children.Add(patternCustomize);
            patternPanel.Children.Add(patternInfo);

            // Preview + actions
            preview = new LedStrip(18);
            previewRpm = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Consolas") };
            var previewBody = new StackPanel { Margin = new Thickness(0, 14, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
            previewBody.Children.Add(preview);
            previewBody.Children.Add(previewRpm);

            var actions = new WrapPanel();
            var save = MakeButton("Save override", SaveEditing, 8);
            save.FontWeight = FontWeights.SemiBold;
            actions.Children.Add(save);
            actions.Children.Add(MakeButton("Cancel", CloseEditor, 8));
            deleteButton = MakeButton("Delete override", () => Delete(editing.CarKey, editing.CarName), 8);
            actions.Children.Add(deleteButton);

            var editorBody = new StackPanel();
            editorBody.Children.Add(Small("EDIT OVERRIDE"));
            editorBody.Children.Add(editorTitle);
            editorBody.Children.Add(kindRow);
            editorBody.Children.Add(offsetPanel);
            editorBody.Children.Add(customPanel);
            editorBody.Children.Add(patternPanel);
            editorBody.Children.Add(previewBody);
            editorBody.Children.Add(actions);
            editorCard = Card(editorBody, new Thickness(0, 0, 0, 12));
            editorCard.BorderBrush = Accent;
            editorCard.Visibility = Visibility.Collapsed;
            Children.Add(editorCard);

            // ----- Saved overrides -----
            Children.Add(new TextBlock { Text = "Saved overrides", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
            list = new StackPanel();
            Children.Add(list);

            frameTimer.Tick += (s, e) => RenderPreview();
            carTimer.Tick += (s, e) => RefreshCurrentCar();
            Loaded += (s, e) => { carTimer.Start(); RefreshCurrentCar(); RefreshList(); };
            Unloaded += (s, e) => { carTimer.Stop(); frameTimer.Stop(); };
        }

        // ---------- Current car ----------

        private void RefreshCurrentCar()
        {
            var key = plugin.CurrentCarKey;
            var ov = plugin.GetOverride(key);
            var summary = ov?.Summary;
            if (key == shownCarKey && summary == shownSummary && carButtons.Children.Count > 0) return;
            shownCarKey = key;
            shownSummary = summary;

            carButtons.Children.Clear();
            if (key == null)
            {
                carTitle.Text = "No car yet";
                carInfo.Text = "Get in a car in any game and it shows up here.";
                return;
            }
            carTitle.Text = $"{plugin.CurrentCarName}  ({plugin.CurrentGame})";
            var baseShift = plugin.CurrentBaseLayout?.ShiftRpm ?? 0;
            carInfo.Text = $"Lights from {plugin.CurrentBaseSource}. Shift point {baseShift} rpm." +
                           (ov != null ? $"\nOverride: {ov.Summary}." : "\nNo override.");
            if (ov == null)
            {
                carButtons.Children.Add(MakeButton("Shift this car's lights earlier / later", () => OpenEditor(plugin.NewOverrideForCurrentCar(OverrideKind.Offset)), 8));
                carButtons.Children.Add(MakeButton("Set each LED for this car", () => OpenEditor(plugin.NewOverrideForCurrentCar(OverrideKind.Custom)), 8));
                carButtons.Children.Add(MakeButton("Use a pattern for this car", () => OpenEditor(plugin.NewOverrideForCurrentCar(OverrideKind.Pattern)), 8));
            }
            else
            {
                carButtons.Children.Add(MakeButton("Edit override", () => OpenEditor(plugin.GetOverride(key)), 8));
                carButtons.Children.Add(MakeButton("Delete override", () => Delete(key, plugin.CurrentCarName), 8));
            }
        }

        // ---------- Editor ----------

        private void OpenEditor(CarOverride o)
        {
            if (o == null) return;
            editing = o;
            editBase = o.CarKey == plugin.CurrentCarKey ? plugin.CurrentBaseLayout?.Clone() : null;
            if (editing.Kind == OverrideKind.Custom && editing.Custom == null)
            {
                editing.Custom = editBase?.Clone() ?? DefaultLayout();
                editing.Custom.Gears = null; // custom lights are the same in every gear
            }
            if (editing.Kind == OverrideKind.Pattern && editing.Style == null)
                editing.Style = plugin.Settings.Fallback.Clone();

            loading = true;
            editorTitle.Text = $"{o.CarName}  ({o.Game})";
            kindOffset.IsChecked = o.Kind == OverrideKind.Offset;
            kindCustom.IsChecked = o.Kind == OverrideKind.Custom;
            kindPattern.IsChecked = o.Kind == OverrideKind.Pattern;
            offsetSlider.Value = o.OffsetRpm;
            offsetBox.Text = o.OffsetRpm.ToString("+0;-0;0");
            loading = false;

            resetButton.Visibility = editBase != null ? Visibility.Visible : Visibility.Collapsed;
            deleteButton.Visibility = plugin.GetOverride(o.CarKey) != null ? Visibility.Visible : Visibility.Collapsed;
            if (editing.Custom != null) LoadCustom();
            if (editing.Style != null) LoadPattern();
            UpdateEditorVisibility();
            Changed();
            editorCard.Visibility = Visibility.Visible;
            frameTimer.Start();
            editorCard.BringIntoView();
        }

        private void CloseEditor()
        {
            editing = null;
            editorCard.Visibility = Visibility.Collapsed;
            frameTimer.Stop();
        }

        private void SetKind(OverrideKind kind)
        {
            if (loading || editing == null) return;
            editing.Kind = kind;
            if (kind == OverrideKind.Custom && editing.Custom == null)
            {
                editing.Custom = editBase?.Offset(editing.OffsetRpm) ?? DefaultLayout();
                LoadCustom();
            }
            if (kind == OverrideKind.Pattern && editing.Style == null)
            {
                editing.Style = plugin.Settings.Fallback.Clone();
                LoadPattern();
            }
            UpdateEditorVisibility();
            Changed();
        }

        private void UpdateEditorVisibility()
        {
            offsetPanel.Visibility = editing.Kind == OverrideKind.Offset ? Visibility.Visible : Visibility.Collapsed;
            customPanel.Visibility = editing.Kind == OverrideKind.Custom ? Visibility.Visible : Visibility.Collapsed;
            patternPanel.Visibility = editing.Kind == OverrideKind.Pattern ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoadPattern()
        {
            var st = editing.Style;
            loading = true;
            patternBox.SelectedItem = patternBox.Items.Cast<ComboBoxItem>().First(i => (PatternKind)i.Tag == st.Pattern);
            schemeBox.SelectedItem = schemeBox.Items.Cast<ComboBoxItem>().First(i => (ColorScheme)i.Tag == st.Colors);
            pFlashBox.SelectedItem = pFlashBox.Items.Cast<ComboBoxItem>().First(i => (FlashMode)i.Tag == st.Flash);
            SelectColor(pColor1, st.Color1);
            SelectColor(pColor2, st.Color2);
            SelectColor(pColor3, st.Color3);
            SelectColor(pFlashColor, st.FlashColor);
            pStart.Value = st.StartPercent;
            loading = false;

            bool preset = st.Pattern == PatternKind.SimProPreset;
            patternCustomize.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;
            bool custom = st.Colors == ColorScheme.Custom;
            pColor1Row.Visibility = custom || st.Colors == ColorScheme.Single ? Visibility.Visible : Visibility.Collapsed;
            pColor2Row.Visibility = pColor3Row.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            pFlashColorRow.Visibility = st.Flash == FlashMode.None ? Visibility.Collapsed : Visibility.Visible;
            pStartLabel.Text = $"{st.StartPercent:0}% of the shift point";
        }

        private void SetOffset(int value, bool fromBox = false)
        {
            value = Math.Max(-5000, Math.Min(5000, value));
            editing.OffsetRpm = value;
            loading = true;
            offsetSlider.Value = Math.Max(offsetSlider.Minimum, Math.Min(offsetSlider.Maximum, value));
            if (!fromBox) offsetBox.Text = value.ToString("+0;-0;0");
            loading = false;
            Changed();
        }

        private void SetLedRpm(int i)
        {
            bool ok = int.TryParse(ledRpm[i].Text, out var v) && v >= 0 && v <= 20000;
            ledRpm[i].BorderBrush = ok ? null : ErrorBrush;
            if (!ok) return;
            editing.Custom.Rpm[i] = v;
            if (v > 0 && editing.Custom.Colors[i] == LedPalette.Off)
            {
                editing.Custom.Colors[i] = LedPalette.Green;
                loading = true; SelectColor(ledColor[i], LedPalette.Green); loading = false;
            }
            Changed();
        }

        private void LoadCustom()
        {
            var c = editing.Custom;
            loading = true;
            for (int i = 0; i < ledRpm.Length; i++)
            {
                ledRpm[i].Text = c.Rpm[i].ToString();
                ledRpm[i].BorderBrush = null;
                SelectColor(ledColor[i], c.Rpm[i] > 0 ? c.Colors[i] : LedPalette.Off);
            }
            flashRpmBox.Text = c.FlashRpm.ToString();
            flashRpmBox.IsEnabled = flashColorBox.IsEnabled = c.FlashRpm > 0;
            flashModeBox.SelectedIndex = c.FlashRpm <= 0 ? 0 : c.FlashBlinkUnits > 0 ? 2 : 1;
            SelectColor(flashColorBox, c.FlashColor);
            loading = false;
        }

        private void Changed()
        {
            if (editing == null) return;
            var result = Result();
            preview.Layout = result?.ToPreview();
            if (editing.Kind == OverrideKind.Offset)
                offsetInfo.Text = editBase != null
                    ? $"Shift point {editBase.ShiftRpm} rpm → {result.ShiftRpm} rpm ({editing.OffsetRpm:+0;-0;0} rpm on every LED and the flash)"
                    : $"{editing.OffsetRpm:+0;-0;0} rpm on every LED and the flash. Drive this car to see its RPM values here.";
            if (editing.Kind == OverrideKind.Pattern)
                patternInfo.Text = editBase != null
                    ? $"Keeps the car's own shift point ({editBase.ShiftRpm} rpm); only the look changes."
                    : "Keeps the car's own shift point; drive this car to preview it here.";
        }

        /// <summary>What the car's lights will be with the override being edited (null when unknown).</summary>
        private RpmLayout Result() => editing.Apply(editBase, plugin.PresetTemplate);

        private void SaveEditing()
        {
            if (editing == null) return;
            if (editing.Kind != OverrideKind.Custom) editing.Custom = null;
            if (editing.Kind != OverrideKind.Pattern) editing.Style = null;
            plugin.SaveOverride(editing);
            CloseEditor();
            shownSummary = "(refresh)";
            RefreshCurrentCar();
            RefreshList();
        }

        private void Delete(string carKey, string name)
        {
            if (MessageBox.Show($"Delete the override for {name}? Its lights go back to the car's default.", "FXPro RPM Sync",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            plugin.DeleteOverride(carKey);
            if (editing?.CarKey == carKey) CloseEditor();
            shownSummary = "(refresh)";
            RefreshCurrentCar();
            RefreshList();
        }

        private void RenderPreview()
        {
            var r = editing == null ? null : Result();
            if (r == null) { previewRpm.Text = "Preview available once you've driven this car"; return; }
            double t = clock.Elapsed.TotalSeconds;
            bool blinkOn = ((int)(t * 1000 / (Math.Max(1, r.FlashBlinkUnits) * RpmLightsMapper.BlinkMsPerUnit))) % 2 == 0;
            var p = preview.Layout;
            double first = p?.Fractions.Where(f => f > 0).DefaultIfEmpty(0.8).Min() ?? 0.8;
            double frac = LedStrip.SweepRpm(t, first);
            preview.Render(frac, blinkOn);
            previewRpm.Text = $"{frac * r.ShiftRpm:0} rpm";
        }

        private static RpmLayout DefaultLayout() =>
            RpmLayout.FromPattern(LedPatterns.Build(new FallbackStyle { Pattern = PatternKind.LeftToRight }), 7000, 0);

        // ---------- Saved list ----------

        private void RefreshList()
        {
            list.Children.Clear();
            var all = plugin.AllOverrides();
            if (all.Count == 0)
            {
                list.Children.Add(Muted("No overrides yet.", new Thickness(0)));
                return;
            }
            foreach (var o in all)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var captured = o;
                buttons.Children.Add(MakeButton("Edit", () => OpenEditor(plugin.GetOverride(captured.CarKey)), 6));
                buttons.Children.Add(MakeButton("Delete", () => Delete(captured.CarKey, captured.CarName), 0));
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);

                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = $"{o.CarName}  ({o.Game})", FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = $"{o.Summary}  ·  updated {o.UpdatedUtc.ToLocalTime():g}", Opacity = 0.7, FontSize = 11 });
                row.Children.Add(text);
                var card = Card(row, new Thickness(0, 0, 0, 6));
                card.Width = 720;
                list.Children.Add(card);
            }
        }

        // ---------- Helpers ----------

        private static Border Card(UIElement child, Thickness margin) => new Border
        {
            Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = margin, Child = child, MaxWidth = 1060,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        private static TextBlock Muted(string text, Thickness margin) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = margin, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

        private static TextBlock Small(string text) =>
            new TextBlock { Text = text, FontSize = 10, Opacity = 0.55, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };

        private static Button MakeButton(string text, Action onClick, double rightMargin)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, rightMargin, 4), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) => onClick();
            return b;
        }

        private ComboBox PatternColor(Action<string> set)
        {
            var box = ColorPicker(compact: false, includeOff: false);
            box.SelectionChanged += (s, e) => { if (!loading && box.SelectedItem is ComboBoxItem it) { set((string)it.Tag); Changed(); } };
            return box;
        }

        private static FrameworkElement FormRow(string label, UIElement control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var l = new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            if (control is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(control);
            return row;
        }

        private static void AddToGrid(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static ComboBox ColorPicker(bool compact, bool includeOff)
        {
            var box = new ComboBox { Margin = new Thickness(2), Width = compact ? double.NaN : 140 };
            var entries = LedPalette.All.AsEnumerable();
            if (includeOff) entries = new[] { ("Off", LedPalette.Off) }.Concat(entries);
            foreach (var (name, hex) in entries)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = hex == LedPalette.Off ? Frozen(Color.FromRgb(0x33, 0x36, 0x3b)) : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    Stroke = CardBorder,
                });
                if (!compact) content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                box.Items.Add(new ComboBoxItem { Content = content, Tag = hex, ToolTip = name });
            }
            return box;
        }

        private static void SelectColor(ComboBox box, string hex) =>
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, hex, StringComparison.OrdinalIgnoreCase))
                               ?? box.Items[0];

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
