using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class SliderAttribute : Attribute, IElement
{
    public enum SliderType
    {
        Integer,
        Float,
    }

    public readonly double Min;
    public readonly double Max;
    public readonly double Step;
    public readonly SliderType Type;
    public readonly string Label;
    public readonly string Description;

    public SliderAttribute(double min, double max, double step = 1.0, SliderType type = SliderType.Float, string label = null, string description = null)
    {
        Min = min;
        Max = max;
        Step = step;
        Type = type;
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var slider = new Slider
        {
            Minimum = Min,
            Maximum = Max,
            TickFrequency = Step,
            IsSnapToTickEnabled = true,
            Width = 240,
            VerticalAlignment = VerticalAlignment.Center,
            Value = Convert.ToDouble(getter()),
        };

        slider.ValueChanged += (_, _) =>
        {
            if (Type == SliderType.Integer)
                setter((int)Math.Round(slider.Value));
            else
                setter((float)slider.Value);
        };

        return RowBuilder.NewRow(Tools.Tools.GetLabelOrDefault(name, Label), Description, slider);
    }

    public List<Type> SupportedTypes { get; } = new() { typeof(int), typeof(float), typeof(double) };
}
