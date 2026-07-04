using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaColor = Avalonia.Media.Color;

namespace Client2Plugin.Settings.Tools;

public static class Tools
{
    private static readonly Regex UpperCaseWordRegex = new Regex(@"[A-Z][a-z]*", RegexOptions.Compiled);
    private static readonly Regex RxHexDigits = new Regex("^[0-9a-f]+$", RegexOptions.IgnoreCase);

    // Avalonia's Fluent ToolTip trims text after a default MaxWidth, so wrap the description
    // in a TextBlock that wraps and sets a generous MaxWidth. FontSize is set explicitly so
    // the popup doesn't inherit a smaller size from the control it's attached to.
    public static void SetWrappedTooltip(Control control, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ToolTip.SetTip(control, new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
            FontSize = 20,
        });
    }

    public static string GetLabelOrDefault(string name, string label = null)
    {
        Debug.Assert(!string.IsNullOrEmpty(name) && name.Trim().Length != 0);

        if (label != null)
            return label;

        var words = UpperCaseWordRegex.Matches(name).Cast<Match>().Select(m => m.Value).ToArray();
        Debug.Assert(words.Length != 0);

        for (var i = 1; i < words.Length; i++)
            words[i] = words[i].ToLower();

        return string.Join(" ", words);
    }

    public static string ToHexStringRgb(this AvaloniaColor color) =>
        $"{color.R:X2}{color.G:X2}{color.B:X2}";

    public static string ToHexStringRgba(this AvaloniaColor color) =>
        $"{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    public static bool TryParseColorFromHexRgb(this string hex, out AvaloniaColor color)
    {
        var digits = NormalizeHex(hex);
        if (digits != null)
        {
            if (digits.Length == 3)
            {
                color = AvaloniaColor.FromArgb(255, ExpandNibble(digits[0]), ExpandNibble(digits[1]), ExpandNibble(digits[2]));
                return true;
            }
            if (digits.Length == 6)
            {
                color = AvaloniaColor.FromArgb(
                    255,
                    Convert.ToByte(digits.Substring(0, 2), 16),
                    Convert.ToByte(digits.Substring(2, 2), 16),
                    Convert.ToByte(digits.Substring(4, 2), 16));
                return true;
            }
        }

        color = AvaloniaColor.FromArgb(255, 0, 0, 0);
        return false;
    }

    public static bool TryParseColorFromHexRgba(this string hex, out AvaloniaColor color)
    {
        var digits = NormalizeHex(hex);
        if (digits != null)
        {
            if (digits.Length == 4)
            {
                color = AvaloniaColor.FromArgb(ExpandNibble(digits[3]), ExpandNibble(digits[0]), ExpandNibble(digits[1]), ExpandNibble(digits[2]));
                return true;
            }
            if (digits.Length == 8)
            {
                color = AvaloniaColor.FromArgb(
                    Convert.ToByte(digits.Substring(6, 2), 16),
                    Convert.ToByte(digits.Substring(0, 2), 16),
                    Convert.ToByte(digits.Substring(2, 2), 16),
                    Convert.ToByte(digits.Substring(4, 2), 16));
                return true;
            }
        }

        color = AvaloniaColor.FromArgb(0, 0, 0, 0);
        return false;
    }

    private static string NormalizeHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var trimmed = hex.Trim();
        if (trimmed.StartsWith("#")) trimmed = trimmed.Substring(1);
        return RxHexDigits.IsMatch(trimmed) ? trimmed : null;
    }

    private static byte ExpandNibble(char c)
    {
        var n = Convert.ToByte(c.ToString(), 16);
        return (byte)((n << 4) | n);
    }
}
