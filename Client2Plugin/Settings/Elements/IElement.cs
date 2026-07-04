using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Client2Plugin.Settings.Elements;

internal interface IElement
{
    Control BuildRow(string name, Func<object> getter, Action<object> setter);
    List<Type> SupportedTypes { get; }
}
