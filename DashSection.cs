using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace User.FXProRpmSync
{
    /// <summary>"Dash per car" part of the settings page: current car, dash gallery, saved cars.</summary>
    public class DashSection : StackPanel
    {
        private static readonly Brush CardBackground = Frozen(Color.FromArgb(0x14, 0xff, 0xff, 0xff));
        private static readonly Brush CardBorder = Frozen(Color.FromArgb(0x26, 0xff, 0xff, 0xff));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x3d, 0x8b, 0xfd));
        private static readonly Dictionary<string, ImageSource> thumbs = new Dictionary<string, ImageSource>();

        private readonly FXProRpmSyncPlugin plugin;
        private readonly TextBlock carTitle, carInfo;
        private readonly StackPanel wheelShows, savedShows;
        private readonly WrapPanel carButtons, gallery;
        private readonly Border galleryCard;
        private readonly StackPanel list;
        private readonly List<(string Id, Border Card)> galleryCards = new List<(string, Border)>();
        private string shownKey, shownState;

        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public DashSection(FXProRpmSyncPlugin plugin)
        {
            this.plugin = plugin;
            Margin = new Thickness(0, 0, 0, 24);

            Children.Add(new TextBlock { Text = "Dash per car", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            Children.Add(Muted("Shows each car's dash on the wheel's screen when you get in it. Pick a dash with the wheel's dash button " +
                               "while driving and it's remembered for that car, or choose one below. The dash button keeps cycling " +
                               "through your preset's dashes as usual. Your SimPro preset isn't changed; its dash order is restored " +
                               "when SimHub exits.", new Thickness(0, 0, 0, 12)));

            var enabled = new CheckBox { Content = "Switch the wheel's dash per car", IsChecked = plugin.Settings.DashSwitching, Margin = new Thickness(0, 0, 0, 6) };
            enabled.Checked += (s, e) => { plugin.Settings.DashSwitching = true; plugin.SaveSettings(); plugin.Reapply(); };
            enabled.Unchecked += (s, e) => { plugin.Settings.DashSwitching = false; plugin.SaveSettings(); plugin.Reapply(); };
            Children.Add(enabled);

            var learn = new CheckBox
            {
                Content = "Learn from the dash button (the last dash you pick while driving a car is used for it next time)",
                IsChecked = plugin.Settings.LearnDashes, Margin = new Thickness(0, 0, 0, 12),
            };
            learn.Checked += (s, e) => { plugin.Settings.LearnDashes = true; plugin.SaveSettings(); };
            learn.Unchecked += (s, e) => { plugin.Settings.LearnDashes = false; plugin.SaveSettings(); };
            Children.Add(learn);

            // ----- Current car -----
            carTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
            carInfo = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
            wheelShows = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            savedShows = new StackPanel();
            var shows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            shows.Children.Add(wheelShows);
            shows.Children.Add(savedShows);
            carButtons = new WrapPanel();
            var carBody = new StackPanel();
            carBody.Children.Add(Small("CURRENT CAR"));
            carBody.Children.Add(carTitle);
            carBody.Children.Add(carInfo);
            carBody.Children.Add(shows);
            carBody.Children.Add(carButtons);
            Children.Add(Card(carBody, new Thickness(0, 0, 0, 12)));

            // ----- Gallery -----
            gallery = new WrapPanel();
            foreach (var dash in DashCatalog.All.Where(d => d.Id != DashCatalog.SettingsPageId))
            {
                var body = new StackPanel { Width = 160 };
                body.Children.Add(Thumb(dash.Id, 160));
                body.Children.Add(new TextBlock { Text = dash.Name, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                var card = new Border
                {
                    Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6),
                    Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand, Child = body, ToolTip = $"Use {dash.Name} for this car",
                };
                var id = dash.Id;
                card.MouseLeftButtonUp += (s, e) => { plugin.PickDashForCurrentCar(id); shownState = null; };
                galleryCards.Add((id, card));
                gallery.Children.Add(card);
            }
            var galleryBody = new StackPanel();
            galleryBody.Children.Add(Small("CHOOSE A DASH FOR THIS CAR"));
            galleryBody.Children.Add(gallery);
            galleryCard = Card(galleryBody, new Thickness(0, 0, 0, 12));
            galleryCard.Visibility = Visibility.Collapsed;
            Children.Add(galleryCard);

            // ----- Saved -----
            Children.Add(new TextBlock { Text = "Saved cars", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });
            list = new StackPanel();
            Children.Add(list);

            timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { timer.Start(); Refresh(); RefreshList(); };
            Unloaded += (s, e) => timer.Stop();
        }

        private void Refresh()
        {
            var key = plugin.DashCarKey;
            var saved = plugin.GetCarDash(key);
            var state = $"{key}|{plugin.WheelDash}|{saved?.DashId}|{saved?.Learned}|{plugin.DashStatus}";
            if (state == shownState) return;
            bool listChanged = shownState == null || saved?.DashId != shownSavedDash || key != shownKey;
            shownState = state;
            shownKey = key;
            shownSavedDash = saved?.DashId;

            wheelShows.Children.Clear();
            savedShows.Children.Clear();
            carButtons.Children.Clear();
            wheelShows.Children.Add(Small("WHEEL IS SHOWING"));
            wheelShows.Children.Add(DashTile(plugin.WheelDash, "SimPro not connected"));

            if (key == null)
            {
                carTitle.Text = "No car yet";
                carInfo.Text = "Get in a car in any game and it shows up here.";
                galleryCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                carTitle.Text = $"{plugin.CurrentCarNameForDash}  ({plugin.CurrentGameForDash})";
                carInfo.Text = plugin.DashStatus;
                savedShows.Children.Add(Small(saved == null ? "SAVED FOR THIS CAR" : saved.Learned ? "SAVED FOR THIS CAR (LEARNED)" : "SAVED FOR THIS CAR (PICKED)"));
                savedShows.Children.Add(DashTile(saved?.DashId, "None: the wheel stays on its current dash"));

                if (plugin.WheelDash != null && plugin.WheelDash != saved?.DashId && plugin.WheelDash != DashCatalog.SettingsPageId)
                    carButtons.Children.Add(MakeButton($"Keep {DashCatalog.NameOf(plugin.WheelDash)} for this car", () => { plugin.KeepWheelDashForCurrentCar(); shownState = null; }));
                carButtons.Children.Add(MakeButton(galleryCard.Visibility == Visibility.Visible ? "Hide dashes" : "Choose a dash…", () =>
                {
                    galleryCard.Visibility = galleryCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    shownState = null;
                }));
                if (saved != null)
                    carButtons.Children.Add(MakeButton("Forget this car's dash", () => { plugin.DeleteCarDash(key); shownState = null; }));
            }

            foreach (var (id, card) in galleryCards)
                card.BorderBrush = id == saved?.DashId ? Accent : CardBorder;
            if (listChanged) RefreshList();
        }

        private string shownSavedDash;

        private void RefreshList()
        {
            list.Children.Clear();
            var all = plugin.AllCarDashes();
            if (all.Count == 0)
            {
                list.Children.Add(Muted("No cars yet.", new Thickness(0)));
                return;
            }
            foreach (var d in all)
            {
                var row = new DockPanel();
                var captured = d;
                var delete = MakeButton("Forget", () => { plugin.DeleteCarDash(captured.CarKey); RefreshList(); shownState = null; });
                delete.Margin = new Thickness(20, 0, 0, 0);
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);

                var thumb = Thumb(d.DashId, 80);
                thumb.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(thumb, Dock.Left);
                row.Children.Add(thumb);

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = $"{d.CarName}  ({d.Game})", FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = $"{DashCatalog.NameOf(d.DashId)}  ·  {(d.Learned ? "learned from the dash button" : "picked")}  ·  {d.UpdatedUtc.ToLocalTime():g}",
                    Opacity = 0.7, FontSize = 11,
                });
                row.Children.Add(text);
                var card = Card(row, new Thickness(0, 0, 0, 6));
                card.Padding = new Thickness(8);
                card.Width = 720;
                list.Children.Add(card);
            }
        }

        // ---------- Helpers ----------

        private static FrameworkElement DashTile(string dashId, string noneText)
        {
            var p = new StackPanel { Width = 200 };
            if (dashId == null)
            {
                p.Children.Add(new Border
                {
                    Width = 200, Height = 120, CornerRadius = new CornerRadius(4), BorderBrush = CardBorder, BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = noneText, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) },
                });
                return p;
            }
            p.Children.Add(Thumb(dashId, 200));
            p.Children.Add(new TextBlock { Text = DashCatalog.NameOf(dashId), Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold });
            return p;
        }

        /// <summary>SimPro's 800x480 preview of a dash, scaled to the given width.</summary>
        private static FrameworkElement Thumb(string dashId, double width)
        {
            var height = Math.Round(width * 480 / 800);
            var img = ThumbSource(dashId);
            if (img == null)
                return new Border
                {
                    Width = width, Height = height, CornerRadius = new CornerRadius(4), Background = CardBackground,
                    Child = new TextBlock { Text = DashCatalog.NameOf(dashId), Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
            return new Image { Source = img, Width = width, Height = height, Stretch = Stretch.Uniform };
        }

        private static ImageSource ThumbSource(string dashId)
        {
            if (dashId == null) return null;
            lock (thumbs)
            {
                if (thumbs.TryGetValue(dashId, out var cached)) return cached;
                ImageSource src = null;
                var path = DashCatalog.Find(dashId).ImagePath;
                if (path != null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(path);
                        bmp.DecodePixelWidth = 200;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        src = bmp;
                    }
                    catch { }
                }
                thumbs[dashId] = src;
                return src;
            }
        }

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

        private static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) => onClick();
            return b;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
