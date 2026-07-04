using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class TextboxAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public TextboxAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var textBox = new TextBox
        {
            Text = (string)getter() ?? string.Empty,
            Width = 280,
            Height = SettingsLayout.ControlHeight,
        };

        textBox.TextChanged += (_, _) => setter(textBox.Text);

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, textBox);
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(string) };
}
