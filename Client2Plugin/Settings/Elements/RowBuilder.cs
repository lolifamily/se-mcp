using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Client2Plugin.Settings.Elements;

internal static class RowBuilder
{
    public const double RowMargin = 2;

    public static readonly IBrush LabelForeground = Brushes.White;

    public static Grid NewRow(string labelText, string tooltip, params Control[] valueControls)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, RowMargin, 0, RowMargin + 2),
            MinHeight = SettingsLayout.RowMinHeight,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition(SettingsLayout.LabelColumnWidth, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var label = new TextBlock
        {
            Text = labelText,
            Foreground = LabelForeground,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var ctrl in valueControls)
            valuePanel.Children.Add(ctrl);

        Grid.SetColumn(valuePanel, 1);
        grid.Children.Add(valuePanel);

        Tools.Tools.SetWrappedTooltip(grid, tooltip);

        return grid;
    }
}
