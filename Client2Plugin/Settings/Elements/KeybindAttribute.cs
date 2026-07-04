using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Client2Plugin.Settings.Tools;
using Client2Plugin.Tools;
using Keen.Game2.Client.UI.Library.Dialogs.InputCompositionDialog;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;
using Keen.VRage.Library.Diagnostics;

namespace Client2Plugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class KeybindAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public KeybindAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        var label = Tools.Tools.GetLabelOrDefault(name, Label);

        var button = new Button
        {
            Content = ((Binding)getter()).ToString(),
            Padding = new Thickness(12, 4, 12, 4),
            MinWidth = 220,
            Height = SettingsLayout.ControlHeight,
        };

        button.Click += (_, _) => OpenCompositionDialog(label, getter, setter, button);

        button.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right) return;
            setter(new Binding());
            button.Content = ((Binding)getter()).ToString();
            e.Handled = true;
        };

        var tooltip = (string.IsNullOrEmpty(Description) ? "" : Description + "\n") +
                      "Click to bind a key (press Esc to cancel). Right-click to clear.";
        return RowBuilder.NewRow(label, tooltip, button);
    }

    private void OpenCompositionDialog(string label, Func<object> getter, Action<object> setter, Button button)
    {
        var sharedUi = GameAccess.GetSharedUI();
        var factory = GameAccess.GetViewModelFactory();
        if (sharedUi == null || factory == null)
        {
            Log.Default.WriteLine(LogSeverity.Warning,
                $"[{Plugin.Name}] Cannot open keybind dialog: SharedUI or ViewModelFactory unavailable");
            return;
        }

        // Build a throwaway action definition: the composition dialog uses it only for
        // input-type validation and display formatting.
        var action = new InputActionDefinition(label, InputType.Digital);
        var currentControl = ((Binding)getter()).ToInputControl(action);

        var dialog = new InputCompositionDialogViewModel(
            currentBinding: currentControl,
            action: action,
            onBindingConfirmed: control =>
            {
                var newBinding = Binding.FromInputControl(control);
                setter(newBinding);
                button.Content = newBinding.ToString();
            },
            onCancelled: () => { },
            factory: factory);

        sharedUi.ShowDialog(dialog);
    }

    private static readonly List<Type> SupportedTypesList = new() { typeof(Binding) };
    public List<Type> SupportedTypes => SupportedTypesList;
}
