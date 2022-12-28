using FsBeatmapProcessor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_trainer
{
    internal class JunUtils
    {
        public static T Clamp<T>(T val, T min, T max) where T : IComparable
        {
            return val.CompareTo(max) > 0 ? max : val.CompareTo(min) < 0 ? min : val;
        }

        public static decimal Quantize(decimal value, decimal step)
        {
            value += step / 2; // make function symmetrical
            int steps = (int)(value / step);
            return steps * step;
        }

        public static string FullPathFromSongsFolder(string path) => Path.Combine(Properties.Settings.Default.SongsFolder, path);

        public static string NormalizeText(string str)
        {
            return str.Replace("\"", "").Replace("*", "").Replace("\\", "").Replace("/", "").Replace("?", "").Replace("<", "").Replace(">", "").Replace("|", "").Replace(":", "");
        }

        public static string GetBeatmapDirectoryName(Beatmap map)
        {
            return Path.GetDirectoryName(map.Filename);
        }

        public static GraphicsPath RoundedRect(RectangleF bounds, int radius)
        {
            var diameter = radius * 2;
            var size = new Size(diameter, diameter);
            var arc = new RectangleF(bounds.Location, size);
            var path = new GraphicsPath();

            if (radius == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);

            path.CloseFigure();
            return path;
        }
        public static void ColorToHSV(Color color, out double hue, out double saturation, out double value)
        {
            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            int min = Math.Min(color.R, Math.Min(color.G, color.B));

            hue = color.GetHue();
            saturation = (max == 0) ? 0 : 1d - (1d * min / max);
            value = max / 255d;
        }

        // values are between:
        // [0, 360] for hue
        // [0, 100] for saturation
        // [0, 100] for value
        public static Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            int v = Convert.ToInt32(value);
            int p = Convert.ToInt32(value * (1 - saturation));
            int q = Convert.ToInt32(value * (1 - f * saturation));
            int t = Convert.ToInt32(value * (1 - (1 - f) * saturation));

            // please use switches bro it's more uptimized than a bunch of nested if statements
            switch (hi)
            {
                case 0:
                    return Color.FromArgb(255, v, t, p);
                case 1:
                    return Color.FromArgb(255, q, v, p);
                case 2:
                    return Color.FromArgb(255, p, v, t);
                case 3:
                    return Color.FromArgb(255, p, q, v);
                case 4:
                    return Color.FromArgb(255, t, p, v);
                default:
                    return Color.FromArgb(255, v, p, q);
            }
        }

        public static Color LerpColor(Color StartColor, Color EndColor, decimal Progress)
        {
            var startSat = StartColor.GetSaturation();
            var startHue = StartColor.GetHue();
            var startBri = StartColor.GetBrightness();
            var endSat = EndColor.GetSaturation();
            var endHue = EndColor.GetHue();
            var endBri = EndColor.GetBrightness();
            var currentSat = startSat + (float)Progress * (endSat - startSat);
            var currentHue = startHue + (float)Progress * (endHue - startHue);
            var currentBri = startBri + (float)Progress * (endBri - startBri);
            return JunUtils.ColorFromHSV(currentHue, currentSat, currentBri);
        }
    }
}
