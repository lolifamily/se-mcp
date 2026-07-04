using System.Collections.Generic;
using System.Xml.Serialization;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;

namespace Client2Plugin.Settings.Tools;

// SE2-compatible keybind representation.
// Persists as Windows virtual-key codes so it round-trips through
// InputControlComposer without any Avalonia dependency.
public struct Binding
{
    // Windows VK code of the main key. Matches InputId.Index for keyboard inputs
    // (KeyboardInputs uses VK codes directly). 0 means unbound.
    public int Vk;
    public bool Ctrl;
    public bool Alt;
    public bool Shift;

    [XmlIgnore]
    public bool IsBound => Vk != 0;

    public Binding(int vk = 0, bool ctrl = false, bool alt = false, bool shift = false)
    {
        Vk = vk;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
    }

    public override string ToString()
    {
        if (!IsBound)
            return "None";

        var toInputControl = ToInputControl(new InputActionDefinition("binding", InputType.Digital));
        return toInputControl?.GuiString ?? $"VK {Vk}";
    }

    // Builds an SE2 InputControl for this binding. Returns null if unbound or if
    // the composer cannot form a valid control for the given action.
    public InputControl ToInputControl(InputActionDefinition action)
    {
        if (!IsBound)
            return null;

        var mainInput = new DigitalInput(Vk, GenericDeviceClass.Keyboard);
        var modifiers = new List<InputId>();
        if (Ctrl) modifiers.Add(KeyboardInputs.Control);
        if (Alt) modifiers.Add(KeyboardInputs.Alt);
        if (Shift) modifiers.Add(KeyboardInputs.Shift);

        var composer = InputControlComposer.KeyboardDefault;
        composer.TryCompose(action, mainInput, modifiers, out var control);
        return control;
    }

    // Inverse of ToInputControl: extracts the main key + modifier flags back out.
    public static Binding FromInputControl(InputControl control)
    {
        var binding = new Binding();
        if (control == null)
            return binding;

        foreach (var input in control.Inputs)
        {
            if (input == KeyboardInputs.Control) binding.Ctrl = true;
            else if (input == KeyboardInputs.Alt) binding.Alt = true;
            else if (input == KeyboardInputs.Shift) binding.Shift = true;
            else binding.Vk = input.Index;
        }
        return binding;
    }
}
