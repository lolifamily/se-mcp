using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class SeparatorAttribute : Attribute, IElement
{
    public readonly string Caption;

    public SeparatorAttribute(string caption = null)
    {
        Caption = caption;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 10, 0, 4),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        if (!string.IsNullOrEmpty(Caption))
        {
            var label = new TextBlock
            {
                Text = Caption,
                Foreground = Brushes.Orange,
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        var line = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x33, 0xE0, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);

        return grid;
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(object) };
}
