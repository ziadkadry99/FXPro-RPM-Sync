using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace User.FXProRpmSync
{
    /// <summary>Draws the wheel's 15 rev LEDs for a layout at a given RPM (as a fraction of the shift point).</summary>
    public class LedStrip : Grid
    {
        private readonly Ellipse[] leds = new Ellipse[RpmLightsMapper.WheelLeds];
        private static readonly Brush UnlitBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3b)));
        private static readonly Brush UnusedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1b, 0x1d, 0x20)));
        private static readonly Brush UnusedStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x2c, 0x2f, 0x33)));

        public LedLayout Layout { get; set; }

        public LedStrip(double ledSize)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < leds.Length; i++)
            {
                leds[i] = new Ellipse
                {
                    Width = ledSize,
                    Height = ledSize,
                    Margin = new Thickness(ledSize * 0.18),
                    StrokeThickness = 1,
                };
                panel.Children.Add(leds[i]);
            }
            Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0e, 0x0f, 0x11)),
                CornerRadius = new CornerRadius(ledSize),
                Padding = new Thickness(ledSize * 0.5, ledSize * 0.35, ledSize * 0.5, ledSize * 0.35),
                Child = panel,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        /// <param name="rpm">Current RPM as a fraction of the shift point (1.0 = shift point).</param>
        /// <param name="blinkPhaseOn">Blink state for blinking flashes.</param>
        public void Render(double rpm, bool blinkPhaseOn)
        {
            var layout = Layout;
            if (layout == null) return;

            bool flashing = layout.FlashColor != null && rpm >= 1.0;
            for (int i = 0; i < leds.Length; i++)
            {
                double f = layout.Fractions[i];
                var led = leds[i];
                if (flashing)
                {
                    bool on = !layout.FlashBlinks || blinkPhaseOn;
                    SetLed(led, on ? layout.FlashColor : null, f > 0 || on);
                }
                else if (f <= 0)
                {
                    led.Fill = UnusedBrush;
                    led.Stroke = UnusedStroke;
                    led.Effect = null;
                }
                else SetLed(led, rpm >= f ? layout.Colors[i] : null, true);
            }
        }

        private static void SetLed(Ellipse led, string hex, bool used)
        {
            if (hex == null)
            {
                led.Fill = used ? UnlitBrush : UnusedBrush;
                led.Stroke = UnusedStroke;
                led.Effect = null;
                return;
            }
            var look = LitLook(hex, led.Width);
            if (led.Fill == look.Fill) return;
            led.Fill = look.Fill;
            led.Stroke = look.Stroke;
            led.Effect = look.Glow;
        }

        private static readonly System.Collections.Generic.Dictionary<string, (Brush Fill, Brush Stroke, Effect Glow)> Looks =
            new System.Collections.Generic.Dictionary<string, (Brush, Brush, Effect)>();

        private static (Brush Fill, Brush Stroke, Effect Glow) LitLook(string hex, double size)
        {
            var key = hex + "|" + size;
            if (Looks.TryGetValue(key, out var look)) return look;
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var glow = new DropShadowEffect { Color = c, BlurRadius = size * 0.9, ShadowDepth = 0, Opacity = 0.9 };
            glow.Freeze();
            look = (Freeze(new SolidColorBrush(c)),
                    Freeze(new SolidColorBrush(Color.FromRgb((byte)(c.R / 2 + 127), (byte)(c.G / 2 + 127), (byte)(c.B / 2 + 127)))),
                    glow);
            Looks[key] = look;
            return look;
        }

        /// <summary>
        /// The demo rev sweep shared by all previews: climbs from below the first LED to the shift point, holds just
        /// past it (so a flash is visible), then drops. Returns RPM as a fraction of the shift point.
        /// </summary>
        public static double SweepRpm(double seconds, double firstLed)
        {
            const double climb = 2.2, hold = 0.9, drop = 0.35, cycle = climb + hold + drop;
            double low = Math.Max(0, Math.Min(firstLed, 0.95) - 0.06);
            double t = seconds % cycle;
            if (t < climb) return low + (1.0 - low) * (t / climb);
            if (t < climb + hold) return 1.02;
            return low + (1.02 - low) * (1 - (t - climb - hold) / drop);
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
