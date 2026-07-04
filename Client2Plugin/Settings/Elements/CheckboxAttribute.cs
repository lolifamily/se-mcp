using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Client2Plugin.Settings.Tools;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class CheckboxAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public CheckboxAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var checkBox = new CheckBox
        {
            IsChecked = (bool)getter(),
        };

        checkBox.IsCheckedChanged += (_, _) => setter(checkBox.IsChecked ?? false);

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, checkBox);
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(bool) };
}
