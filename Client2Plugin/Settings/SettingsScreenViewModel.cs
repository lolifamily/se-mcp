using System;
using Avalonia.Controls;
using Keen.VRage.UI.Screens;

namespace Client2Plugin.Settings;

internal class SettingsScreenViewModel : ScreenViewModel
{
    public string Title { get; }
    public Action<StackPanel> BuildContent { get; }
    public Action OnClosed { get; }

    public SettingsScreenViewModel(string title, Action<StackPanel> buildContent, Action onClosed = null)
    {
        KeepsOtherScreensVisible = false;
        AllowsInputBelowUI = false;
        AllowsInputFromLowerScreens = false;

        Title = title;
        BuildContent = buildContent;
        OnClosed = onClosed;

        InitializeInputContext();
    }

    public override void OnClose()
    {
        base.OnClose();
        OnClosed?.Invoke();
    }
}
