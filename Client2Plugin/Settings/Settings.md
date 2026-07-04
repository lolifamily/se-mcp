# Settings UI

The template ships with an attribute-driven Settings UI generator. Mark the
properties on `Config` with one of the built-in attributes and a matching row
will be rendered automatically in the settings dialog. The dialog is opened by
Pulsar when the user clicks the plugin's settings button.

## Supported attributes

| Attribute              | Property type                | Description                                                        |
|------------------------|------------------------------|--------------------------------------------------------------------|
| `[Checkbox]`           | `bool`                       | Toggle.                                                            |
| `[Slider(min, max, step, type)]` | `int`, `float`, `double` | Slider with optional integer/float formatting.               |
| `[Textbox]`            | `string`                     | Single-line text input.                                            |
| `[Dropdown]`           | any `enum`                   | Combo box with one entry per enum value (CamelCase → "Camel Case").|
| `[Color]`              | `Avalonia.Media.Color`       | Color swatch + hex text box. Set `hasAlpha: true` for RGBA.        |
| `[Keybind]`            | `Binding`                    | Button that opens SE2's input composition dialog; right-click clears.|
| `[Button]`             | `void` method                | Renders a button that invokes the method.                          |
| `[Separator("caption")]` | any property               | Inserts a labelled separator above the property.                   |

All attributes accept an optional `label` (overrides the property name) and
`description` (used as a tooltip). See [Config.cs](../Config.cs) for
working examples of every attribute.

## Color property persistence

`Avalonia.Media.Color` is not XML-serializable, so `[XmlIgnore]` the public
`Color` property and back it with a packed ARGB `uint` plus a companion public
property that the XML serializer can see:

```csharp
private uint color = 0xFF00FFFFu;

[XmlIgnore]
[Color(description: "RGB color")]
public Color Color
{
    get => Avalonia.Media.Color.FromUInt32(color | 0xFF000000u);
    set => SetField(ref color, value.ToUInt32() | 0xFF000000u);
}

// Serialized to XML instead of the Color property.
public uint ColorPacked { get => color; set => color = value; }
```

## Keybind persistence

`Binding` stores a Windows virtual-key code plus Ctrl/Alt/Shift flags — the same
representation SE2's `KeyboardInputs` uses — so it round-trips cleanly through
`InputControlComposer.KeyboardDefault` and XML-serializes as plain fields.
`Binding.ToInputControl(action)` and `Binding.FromInputControl(control)` bridge
to the game's `InputControl` type when you need to register the binding with
SE2's input system.

## Reacting to the configured keybind

The `[Keybind]` attribute only captures and persists the binding — it does not
wire the key to any action. To actually react when the user presses it, poll
the game's keyboard device from a per-frame Harmony patch.

`IInputManager` is an external service on every `Session`, and its `Keyboard`
property is an `IInputDevice` whose `GetDigitalState(InputId)` returns the
current up/down state. `Binding.Vk` is already an `InputId` index for the
keyboard device, so it plugs straight in:

```csharp
var keyboard = session.Get<IInputManager>().Keyboard;
var binding = Config.Current.Keybind;

var pressed =
    new DigitalInput(binding.Vk, GenericDeviceClass.Keyboard).IsActive(keyboard) &&
    KeyboardInputs.Control.IsActive(keyboard) == binding.Ctrl &&
    KeyboardInputs.Alt.IsActive(keyboard)     == binding.Alt &&
    KeyboardInputs.Shift.IsActive(keyboard)   == binding.Shift;
```

Patch a per-frame method (e.g. `Session.Update`) with a postfix, filter to the
client session (only it has `SessionInGameUISessionComponent`), and compare the
current pressed state to the previous one to edge-trigger once per press rather
than every frame the key is held.

See [Patches/KeybindTestPatch.cs](../Patches/KeybindTestPatch.cs) for a working
end-to-end example that also shows a toast via
`SessionInGameUISessionComponent.DisplayNotification(...)`.

Why poll rather than register an `InputActionDefinition` with an
`InputContext`? That first-class path exists but is content-driven: the
`ActionControlMapping` is loaded from XML and
`ActionInputProcessorBaseComponent.SetMapping` replaces it wholesale, so
injecting a plugin-defined action cleanly would mean hooking the content
pipeline. Polling through `IInputDevice` uses the game's own input abstraction
(no Win32) without that complexity.

## Customizing layout

Dialog size and the width of the label column are defined in
[SettingsLayout.cs](SettingsLayout.cs). Adjust `DialogWidth`, `DialogHeight`,
and `LabelColumnWidth` to fit the amount of content and the fonts used by your
plugin.

For deeper customization (fonts, colors, spacing) edit
[SettingsScreen.cs](SettingsScreen.cs) and
[RowBuilder.cs](Elements/RowBuilder.cs).

## Adding your own control types

To support a property type that is not covered by the built-in attributes,
add a new file under [Elements/](Elements/) that implements `IElement` and
derives from `Attribute`. `BuildRow` should return a `Control` built via
`RowBuilder.NewRow(label, tooltip, ...)`.
[`SettingsGenerator`](SettingsGenerator.cs) will pick the new attribute up
automatically — no registration needed.
