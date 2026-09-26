using System;
using System.Collections.Generic;
using System.Linq;

namespace User.FXProRpmSync
{
    public enum PatternKind
    {
        SimProPreset,
        LeftToRight,
        RightToLeft,
        EdgesToCenter,
        CenterToEdges,
        SteppedLeftToRight,
        Middle9LeftToRight,
        Middle9EdgesToCenter,
    }

    public enum ColorScheme { GreenYellowRed, GreenRedBlue, GreenYellowBlue, Single, Custom }

    public enum FlashMode { None, Solid, Blink }

    /// <summary>How cars without real rev-light data are displayed. Serialized in the plugin settings.</summary>
    public class FallbackStyle
    {
        public PatternKind Pattern = PatternKind.SimProPreset;
        public ColorScheme Colors = ColorScheme.GreenYellowRed;
        public string Color1 = LedPalette.Green;
        public string Color2 = LedPalette.Yellow;
        public string Color3 = LedPalette.Red;

        /// <summary>First LED lights at this % of the shift point (the last one at 100%).</summary>
        public double StartPercent = 85;

        public FlashMode Flash = FlashMode.None;
        public string FlashColor = LedPalette.Blue;

        public FallbackStyle Clone() => (FallbackStyle)MemberwiseClone();
    }

    /// <summary>The colors the FX Pro can show (SimPro's LED palette); anything else displays as off.</summary>
    public static class LedPalette
    {
        public const string Red = "#ff0054";
        public const string Orange = "#ff6c00";
        public const string Yellow = "#fffd51";
        public const string Green = "#00ff84";
        public const string Cyan = "#00fffc";
        public const string Blue = "#0006ff";
        public const string Purple = "#6000ff";
        public const string White = "#eeeeee";
        public const string Off = "#000000";

        public static readonly (string Name, string Hex)[] All =
        {
            ("Green", Green), ("Yellow", Yellow), ("Orange", Orange), ("Red", Red),
            ("Cyan", Cyan), ("Blue", Blue), ("Purple", Purple), ("White", White),
        };
    }

    /// <summary>A 15-LED layout: per LED the fraction of the shift point where it lights (0 = unused) and its color.</summary>
    public class LedLayout
    {
        public double[] Fractions = new double[RpmLightsMapper.WheelLeds];
        public string[] Colors = new string[RpmLightsMapper.WheelLeds];
        public string FlashColor;   // null = no flash
        public bool FlashBlinks;
    }

    public static class LedPatterns
    {
        public static readonly (PatternKind Kind, string Title, string Description)[] Catalog =
        {
            (PatternKind.SimProPreset, "Your SimPro preset", "The pattern and colors of the preset selected in SimPro"),
            (PatternKind.LeftToRight, "Left to right", "All 15 LEDs fill from the left"),
            (PatternKind.RightToLeft, "Right to left", "All 15 LEDs fill from the right"),
            (PatternKind.EdgesToCenter, "Edges to center", "Both sides fill in and meet in the middle (Porsche / Ford GT3 style)"),
            (PatternKind.CenterToEdges, "Center to edges", "Starts in the middle and spreads outward"),
            (PatternKind.SteppedLeftToRight, "Stepped", "Blocks of 3 LEDs light together, left to right"),
            (PatternKind.Middle9LeftToRight, "Middle 9, left to right", "Outer 3 LEDs on each side stay off (Ferrari 296 GT3 style)"),
            (PatternKind.Middle9EdgesToCenter, "Middle 9, edges to center", "Outer LEDs off, the middle 9 fill toward the center"),
        };

        public static readonly (ColorScheme Scheme, string Title)[] SchemeCatalog =
        {
            (ColorScheme.GreenYellowRed, "Green, yellow, red"),
            (ColorScheme.GreenRedBlue, "Green, red, blue (F1)"),
            (ColorScheme.GreenYellowBlue, "Green, yellow, blue"),
            (ColorScheme.Single, "Single color"),
            (ColorScheme.Custom, "Custom"),
        };

        /// <summary>
        /// Order in which each LED lights: 0 = first, 1 = last (at the shift point), negative = unused.
        /// Not valid for SimProPreset (that one comes from the preset itself).
        /// </summary>
        public static double[] Ranks(PatternKind kind)
        {
            const int n = RpmLightsMapper.WheelLeds, last = n - 1, mid = last / 2;
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                int fromEdge = Math.Min(i, last - i);
                switch (kind)
                {
                    case PatternKind.RightToLeft: r[i] = (last - i) / (double)last; break;
                    case PatternKind.EdgesToCenter: r[i] = fromEdge / (double)mid; break;
                    case PatternKind.CenterToEdges: r[i] = (mid - fromEdge) / (double)mid; break;
                    case PatternKind.SteppedLeftToRight: r[i] = (i / 3) / 4.0; break;
                    case PatternKind.Middle9LeftToRight: r[i] = i < 3 || i > 11 ? -1 : (i - 3) / 8.0; break;
                    case PatternKind.Middle9EdgesToCenter: r[i] = i < 3 || i > 11 ? -1 : (fromEdge - 3) / 4.0; break;
                    default: r[i] = i / (double)last; break;
                }
            }
            return r;
        }

        public static string[] SchemeColors(FallbackStyle s)
        {
            switch (s.Colors)
            {
                case ColorScheme.GreenRedBlue: return new[] { LedPalette.Green, LedPalette.Red, LedPalette.Blue };
                case ColorScheme.GreenYellowBlue: return new[] { LedPalette.Green, LedPalette.Yellow, LedPalette.Blue };
                case ColorScheme.Single: return new[] { s.Color1, s.Color1, s.Color1 };
                case ColorScheme.Custom: return new[] { s.Color1, s.Color2, s.Color3 };
                default: return new[] { LedPalette.Green, LedPalette.Yellow, LedPalette.Red };
            }
        }

        /// <summary>Builds the layout of a generated pattern (everything except SimProPreset).</summary>
        public static LedLayout Build(FallbackStyle s)
        {
            var ranks = Ranks(s.Pattern);
            var colors = SchemeColors(s);
            double start = Math.Min(Math.Max(s.StartPercent, 1), 100) / 100.0;
            var layout = new LedLayout
            {
                FlashColor = s.Flash == FlashMode.None ? null : s.FlashColor,
                FlashBlinks = s.Flash == FlashMode.Blink,
            };
            for (int i = 0; i < ranks.Length; i++)
            {
                if (ranks[i] < 0) { layout.Fractions[i] = 0; layout.Colors[i] = LedPalette.Off; continue; }
                layout.Fractions[i] = start + (1 - start) * ranks[i];
                // Color zones by lighting order: first third, middle third, last third.
                layout.Colors[i] = colors[ranks[i] < 1 / 3.0 ? 0 : ranks[i] < 2 / 3.0 ? 1 : 2];
            }
            return layout;
        }
    }
}
