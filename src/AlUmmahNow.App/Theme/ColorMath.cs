using System;

namespace AlUmmahNow.App.Theme
{
    /// <summary>Colors (ARGB) over each other, and their contrast, for the themes and for what CI's scenarios read from the screen.</summary>
    public static class ColorMath
    {
        /// <summary>One color over another, as one opaque color: what is behind it does not show through.</summary>
        public static uint Over(uint color, uint surface, double alpha)
        {
            uint Channel(int shift) => (uint)Math.Round((((color >> shift) & 255) * alpha) + (((surface >> shift) & 255) * (1 - alpha))) << shift;
            return 0xFF000000u | Channel(16) | Channel(8) | Channel(0);
        }

        /// <summary>The contrast of two opaque colors (WCAG); readable text has at least 4.5, a control's parts at least 3.</summary>
        public static double Contrast(uint a, uint b)
        {
            static double Channel(uint v) => v / 255.0 <= 0.03928 ? v / 255.0 / 12.92 : Math.Pow(((v / 255.0) + 0.055) / 1.055, 2.4);
            static double Light(uint c) => (0.2126 * Channel((c >> 16) & 255)) + (0.7152 * Channel((c >> 8) & 255)) + (0.0722 * Channel(c & 255));
            var (x, y) = (Light(a), Light(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
    }
}
