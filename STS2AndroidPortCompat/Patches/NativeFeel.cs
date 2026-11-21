using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// One gate for everything a player would recognise as "a port / a mod / a launcher".
/// By default the Android build looks like a native mobile release: no companion-settings
/// rows, no technical loading text, no LAN/debug/retry entry points, no Quit or Patch Notes.
/// Tapping the first header of the in-game Mobile settings tab 7 times turns on developer
/// mode (stored like every other Android-only key), which brings all of it back.
/// </summary>
public static class NativeFeel
{
    public const string DeveloperModeKey = "android_developer_mode";

    public static bool IsAndroid => OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);

    public static bool DeveloperMode => AndroidSettingsBridge.GetBool(DeveloperModeKey, false);

    /// <summary>True when port/mod/diagnostic UI may be shown (non-Android, or developer mode on).</summary>
    public static bool ShowPortUi => !IsAndroid || DeveloperMode;

    public static void SetDeveloperMode(bool enabled)
    {
        AndroidSettingsBridge.SetBool(DeveloperModeKey, enabled);
        PatchHelper.Log($"Developer mode {(enabled ? "enabled" : "disabled")}.");
    }

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "RefreshButtons", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "OnSubmenuStackChanged", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));

        var debugLabelType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Debug.NDebugInfoLabelManager");
        if (debugLabelType != null)
            PatchHelper.Patch(harmony, debugLabelType, "UpdateText", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(DebugInfoUpdateTextPostfix)));

        var settingsScreenType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen");
        if (settingsScreenType != null)
            PatchHelper.Patch(harmony, settingsScreenType, "_Ready", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(SettingsScreenReadyPostfix)));
    }

    // STS1 mobile's main menu has no Quit / Patch Notes; the OS closes the app. Multiplayer on
    // this port is LAN-only (no Steam), so its entry is a developer-mode feature too.
    public static void MainMenuPostfix(NMainMenu __instance)
    {
        try
        {
            if (ShowPortUi)
                return;
            HideButton(Traverse.Create(__instance).Field("_quitButton").GetValue<Control>());
            HideButton(Traverse.Create(__instance).Field("_patchNotesButtonNode").GetValue<Control>());
            HideButton(Traverse.Create(__instance).Field("_multiplayerButton").GetValue<Control>());
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Native main-menu cleanup failed: {exception.Message}");
        }
    }

    // The game's own version/date label stays (the official build shows it); only the
    // "Running modded" badge is suppressed.
    public static void DebugInfoUpdateTextPostfix(Node __instance)
    {
        try
        {
            if (ShowPortUi)
                return;
            if (Traverse.Create(__instance).Field("_moddedWarning").GetValue<Control>() is { } warning)
                warning.Visible = false;
        }
        catch
        {
        }
    }

    public static void SettingsScreenReadyPostfix(Node __instance)
    {
        try
        {
            if (ShowPortUi)
                return;
            HideButton(__instance.GetNodeOrNull<Control>("%Modding"));
            HideButton(__instance.GetNodeOrNull<Control>("%ModdingDivider"));
            HideFeedbackRow(__instance);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Native settings cleanup failed: {exception.Message}");
        }
    }

    // FeedbackScreenPatches suppresses the beta feedback screen, so its settings button
    // would open nothing. Hide the button's own line (and that line's divider) when it
    // sits in a settings list; otherwise hide just the button.
    private static void HideFeedbackRow(Node settingsScreen)
    {
        var button = settingsScreen.GetNodeOrNull<Control>("%FeedbackButton");
        if (button == null)
            return;
        var line = button.GetParent() as Control;
        var list = line?.GetParent();
        if (line != null && list is BoxContainer && line is not ScrollContainer)
        {
            HideButton(line);
            HideButton(list.GetNodeOrNull<Control>(line.Name + "Divider"));
        }
        else
        {
            HideButton(button);
        }
    }

    private static void HideButton(Control control)
    {
        if (control != null && GodotObject.IsInstanceValid(control))
            control.Visible = false;
    }
}
