using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Keen.VRage.UI.Screens;

namespace Client2Plugin.Settings;

internal class SettingsScreen : ScreenView
{
    public StackPanel ContentHost { get; private set; }
    private TextBlock titleText;

    public SettingsScreen()
    {
        Build();
    }

    private void Build()
    {
        titleText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 25,
            FontWeight = FontWeight.Normal,
            Foreground = Brushes.White,
            Text = "Settings",
        };

        var closeButton = new Button
        {
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = "X",
        };
        closeButton.Click += (_, _) => Dispose();

        var titleBar = new Grid();
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        var separator = new Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(77, 99, 96)),
            Margin = new Thickness(0, 16, 0, 0),
        };

        ContentHost = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Margin = new Thickness(12, 0, 12, 0),
            [TextElement.FontSizeProperty] = 18d,
        };

        var scrollViewer = new ScrollViewer
        {
            Content = ContentHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(96, 41, 54, 62)),
            Margin = new Thickness(0, 16, 0, 0),
        };

        var layout = new Grid
        {
            Margin = new Thickness(30),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(separator, 1);
        Grid.SetRow(scrollViewer, 2);
        layout.Children.Add(titleBar);
        layout.Children.Add(separator);
        layout.Children.Add(scrollViewer);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(96, 37, 47, 53)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(96, 66, 77, 99)),
            BorderThickness = new Thickness(1.5),
            Width = SettingsLayout.DialogWidth,
            Height = SettingsLayout.DialogHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = layout,
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        root.Children.Add(border);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Transparent;
        Content = root;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is SettingsScreenViewModel vm)
        {
            titleText.Text = vm.Title ?? "Settings";
            ContentHost.Children.Clear();
            vm.BuildContent?.Invoke(ContentHost);
        }
    }
}
