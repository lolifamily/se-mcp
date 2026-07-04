using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Method)]
internal class ButtonAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public ButtonAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 4, 12, 4),
            Height = SettingsLayout.ControlHeight,
        };

        button.Click += (_, _) => ((Action)getter())();

        return RowBuilder.NewRow(string.Empty, Description, button);
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(Delegate) };
}
