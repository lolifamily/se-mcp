using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class DropdownAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    private static readonly Regex UnCamelCaseRegex1 = new(@"(\P{Ll})(\P{Ll}\p{Ll})", RegexOptions.Compiled);
    private static readonly Regex UnCamelCaseRegex2 = new(@"(\p{Ll})(\P{Ll})", RegexOptions.Compiled);

    public DropdownAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    private static string UnCamelCase(string str) =>
        UnCamelCaseRegex2.Replace(UnCamelCaseRegex1.Replace(str, "$1 $2"), "$1 $2");

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var selected = getter();
        var enumType = selected.GetType();
        var names = Enum.GetNames(enumType);
        var values = Enum.GetValues(enumType);

        var comboBox = new ComboBox
        {
            Width = 240,
            Height = SettingsLayout.ControlHeight,
            [TextElement.FontSizeProperty] = 18d,
        };

        for (var i = 0; i < names.Length; i++)
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = UnCamelCase(names[i]), FontSize = 18 },
                Tag = values.GetValue(i),
            });

        for (var i = 0; i < names.Length; i++)
        {
            if (Equals(values.GetValue(i), selected))
            {
                comboBox.SelectedIndex = i;
                break;
            }
        }

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is ComboBoxItem item)
                setter(item.Tag);
        };

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, comboBox);
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(Enum) };
}
