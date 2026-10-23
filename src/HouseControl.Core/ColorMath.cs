namespace HouseControl.Core;

/// <summary>Conversions between the panel's "#rrggbb" and what the platforms take.</summary>
public static class ColorMath
{
    public static (double R, double G, double B)? Rgb(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    public static string Hex(double r, double g, double b)
    {
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        return $"#{Byte(r):x2}{Byte(g):x2}{Byte(b):x2}";
    }

    /// <summary>Hue in degrees 0…360, saturation and value 0…1.</summary>
    public static (double H, double S, double V)? Hsv(string? hex)
    {
        if (Rgb(hex) is not var (r, g, b)) return null;
        double hi = Math.Max(r, Math.Max(g, b)), lo = Math.Min(r, Math.Min(g, b)), d = hi - lo;
        double h = 0;
        if (d > 0)
        {
            if (hi == r) h = 60 * ((g - b) / d % 6);
            else if (hi == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, hi == 0 ? 0 : d / hi, hi);
    }

    /// <summary>The colour of a hue and saturation at full value, as the panel shows it.</summary>
    public static string HexFromHue(double hue, double saturation)
    {
        var h = ((hue % 360) + 360) % 360 / 60;
        var s = Math.Clamp(saturation, 0, 1);
        double c = s, x = c * (1 - Math.Abs(h % 2 - 1)), m = 1 - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Hex(r + m, g + m, b + m);
    }

    /// <summary>CIE 1931 xy of an sRGB colour (Philips' "wide gamut" conversion).</summary>
    public static (double X, double Y)? Xy(string? hex)
    {
        if (Rgb(hex) is not var (r, g, b)) return null;
        static double Linear(double c) => c > 0.04045 ? Math.Pow((c + 0.055) / 1.055, 2.4) : c / 12.92;
        var (lr, lg, lb) = (Linear(r), Linear(g), Linear(b));
        var X = lr * 0.4124 + lg * 0.3576 + lb * 0.1805;
        var Y = lr * 0.2126 + lg * 0.7152 + lb * 0.0722;
        var Z = lr * 0.0193 + lg * 0.1192 + lb * 0.9505;
        var sum = X + Y + Z;
        if (sum <= 0) return (0.3127, 0.3290); // black: the white point
        return (Math.Round(X / sum * 10000) / 10000, Math.Round(Y / sum * 10000) / 10000);
    }

    /// <summary>The sRGB colour of an xy chromaticity at full brightness.</summary>
    public static string HexFromXy(double x, double y)
    {
        if (y <= 0) return "#ffffff";
        double Y = 1.0, X = Y / y * x, Z = Y / y * (1 - x - y);
        var r = X * 3.2406 - Y * 1.5372 - Z * 0.4986;
        var g = -X * 0.9689 + Y * 1.8758 + Z * 0.0415;
        var b = X * 0.0557 - Y * 0.2040 + Z * 1.0570;
        var top = Math.Max(Math.Max(r, g), Math.Max(b, 1e-9));
        (r, g, b) = (Math.Max(0, r / top), Math.Max(0, g / top), Math.Max(0, b / top));
        static double Gamma(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return Hex(Gamma(r), Gamma(g), Gamma(b));
    }

    /// <summary>Kelvin ↔ mired (what Hue uses for white temperature).</summary>
    public static double Mired(double kelvin) => 1_000_000 / Math.Max(1000, kelvin);
    public static double Kelvin(double mired) => 1_000_000 / Math.Max(1, mired);
}
