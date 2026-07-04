using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Client2Plugin.Settings.Tools;
using AvaloniaColor = Avalonia.Media.Color;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class ColorAttribute : Attribute, IElement
{
    private enum Source { Rgb, Hsv, Alpha, Hex }

    public readonly bool HasAlpha;
    public readonly string Label;
    public readonly string Description;

    public ColorAttribute(bool hasAlpha = false, string label = null, string description = null)
    {
        HasAlpha = hasAlpha;
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var currentColor = (AvaloniaColor)getter();
        var (h0, s0, v0) = RgbToHsv(currentColor.R, currentColor.G, currentColor.B);

        var previewBorder = new Border
        {
            Width = 50,
            Height = 50,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.White,
            Background = new SolidColorBrush(currentColor),
        };

        var textBox = new TextBox
        {
            Text = HasAlpha ? currentColor.ToHexStringRgba() : currentColor.ToHexStringRgb(),
            MaxLength = HasAlpha ? 8 : 6,
            Width = HasAlpha ? 170 : 140,
            Height = SettingsLayout.ControlHeight,
        };

        var originalBorderBrush = textBox.BorderBrush;
        var updating = false;

        var rSlider = BuildChannelSlider(0, 255, currentColor.R, out var rValueText);
        var gSlider = BuildChannelSlider(0, 255, currentColor.G, out var gValueText);
        var bSlider = BuildChannelSlider(0, 255, currentColor.B, out var bValueText);
        var hSlider = BuildChannelSlider(0, 360, h0, out var hValueText);
        var sSlider = BuildChannelSlider(0, 100, s0 * 100, out var sValueText);
        var vSlider = BuildChannelSlider(0, 100, v0 * 100, out var vValueText);
        var aSlider = BuildChannelSlider(0, 255, currentColor.A, out var aValueText);
        if (!HasAlpha)
            aSlider.IsEnabled = false;

        void Commit(AvaloniaColor color, Source source)
        {
            if (updating) return;
            updating = true;

            if (source != Source.Rgb)
            {
                rSlider.Value = color.R;
                gSlider.Value = color.G;
                bSlider.Value = color.B;
            }
            if (source != Source.Hsv)
            {
                var (h, s, v) = RgbToHsv(color.R, color.G, color.B);
                hSlider.Value = h;
                sSlider.Value = s * 100;
                vSlider.Value = v * 100;
            }
            if (source != Source.Alpha)
            {
                aSlider.Value = color.A;
            }

            rValueText.Text = color.R.ToString();
            gValueText.Text = color.G.ToString();
            bValueText.Text = color.B.ToString();
            hValueText.Text = ((int)Math.Round(hSlider.Value)).ToString();
            sValueText.Text = ((int)Math.Round(sSlider.Value)).ToString();
            vValueText.Text = ((int)Math.Round(vSlider.Value)).ToString();
            aValueText.Text = color.A.ToString();

            previewBorder.Background = new SolidColorBrush(color);
            if (source != Source.Hex)
                textBox.Text = HasAlpha ? color.ToHexStringRgba() : color.ToHexStringRgb();
            textBox.BorderBrush = originalBorderBrush;
            setter(color);

            updating = false;
        }

        void OnRgbChanged()
        {
            if (updating) return;
            var color = AvaloniaColor.FromArgb(
                (byte)aSlider.Value,
                (byte)rSlider.Value,
                (byte)gSlider.Value,
                (byte)bSlider.Value);
            Commit(color, Source.Rgb);
        }

        void OnHsvChanged()
        {
            if (updating) return;
            var (r, g, b) = HsvToRgb(hSlider.Value, sSlider.Value / 100.0, vSlider.Value / 100.0);
            var color = AvaloniaColor.FromArgb((byte)aSlider.Value, r, g, b);
            Commit(color, Source.Hsv);
        }

        void OnAlphaChanged()
        {
            if (updating) return;
            var color = AvaloniaColor.FromArgb(
                (byte)aSlider.Value,
                (byte)rSlider.Value,
                (byte)gSlider.Value,
                (byte)bSlider.Value);
            Commit(color, Source.Alpha);
        }

        void WatchValue(Slider slider, Action onChange)
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty) onChange();
            };
        }

        WatchValue(rSlider, OnRgbChanged);
        WatchValue(gSlider, OnRgbChanged);
        WatchValue(bSlider, OnRgbChanged);
        WatchValue(hSlider, OnHsvChanged);
        WatchValue(sSlider, OnHsvChanged);
        WatchValue(vSlider, OnHsvChanged);
        WatchValue(aSlider, OnAlphaChanged);

        textBox.TextChanged += (_, _) =>
        {
            if (updating) return;

            var text = textBox.Text ?? string.Empty;
            var ok = HasAlpha
                ? text.TryParseColorFromHexRgba(out var color)
                : text.TryParseColorFromHexRgb(out color);

            if (!ok)
            {
                textBox.BorderBrush = Brushes.Red;
                previewBorder.Background = Brushes.Transparent;
                return;
            }

            Commit(color, Source.Hex);
        };

        var pickerPanel = BuildPickerPanel(HasAlpha,
            rSlider, rValueText, gSlider, gValueText, bSlider, bValueText,
            hSlider, hValueText, sSlider, sValueText, vSlider, vValueText,
            aSlider, aValueText);

        var previewButton = new Button
        {
            Width = 52,
            Height = 52,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = previewBorder,
            Flyout = new Flyout { Content = pickerPanel },
        };

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, previewButton, textBox);
    }

    private static Slider BuildChannelSlider(double min, double max, double initial, out TextBlock valueText)
    {
        valueText = new TextBlock
        {
            Text = ((int)Math.Round(initial)).ToString(),
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        };
        return new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = Math.Max(1, (max - min) / 16.0),
            Value = initial,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control BuildPickerPanel(bool hasAlpha,
        Slider rSlider, TextBlock rText,
        Slider gSlider, TextBlock gText,
        Slider bSlider, TextBlock bText,
        Slider hSlider, TextBlock hText,
        Slider sSlider, TextBlock sText,
        Slider vSlider, TextBlock vText,
        Slider aSlider, TextBlock aText)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8),
        };

        AddChannelRow(grid, 0, "R", rSlider, rText);
        AddChannelRow(grid, 1, "G", gSlider, gText);
        AddChannelRow(grid, 2, "B", bSlider, bText);
        AddChannelRow(grid, 3, "H", hSlider, hText);
        AddChannelRow(grid, 4, "S", sSlider, sText);
        AddChannelRow(grid, 5, "V", vSlider, vText);
        AddChannelRow(grid, 6, "A", aSlider, aText);
        if (!hasAlpha)
            aText.Opacity = 0.5;

        return grid;
    }

    private static void AddChannelRow(Grid grid, int row, string label, Slider slider, TextBlock valueText)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var rowMargin = new Thickness(0, 2, 0, 2);

        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 16,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = rowMargin,
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        slider.Margin = new Thickness(6, 2, 6, 2);
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        valueText.Margin = rowMargin;
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);
    }

    private static (double H, double S, double V) RgbToHsv(byte rByte, byte gByte, byte bByte)
    {
        var r = rByte / 255.0;
        var g = gByte / 255.0;
        var b = bByte / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double h;
        if (delta < 1e-9) h = 0;
        else if (max == r) h = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g) h = 60.0 * ((b - r) / delta + 2.0);
        else h = 60.0 * ((r - g) / delta + 4.0);
        if (h < 0) h += 360.0;

        var s = max < 1e-9 ? 0 : delta / max;
        var v = max;
        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var hPrime = h / 60.0;
        var x = c * (1 - Math.Abs(hPrime % 2 - 1));
        var (r1, g1, b1) = hPrime switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = v - c;
        return (
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(AvaloniaColor) };
}
