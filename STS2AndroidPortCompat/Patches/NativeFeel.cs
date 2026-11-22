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
    private const string FastModeDefaultKey = "mobile_fast_mode_default_applied";

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
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", prefix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuReadyPrefix)), postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "RefreshButtons", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "OnSubmenuStackChanged", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MainMenuPostfix)));

        var debugLabelType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Debug.NDebugInfoLabelManager");
        if (debugLabelType != null)
            PatchHelper.Patch(harmony, debugLabelType, "UpdateText", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(DebugInfoUpdateTextPostfix)));

        var settingsScreenType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen");
        if (settingsScreenType != null)
            PatchHelper.Patch(harmony, settingsScreenType, "_Ready", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(SettingsScreenReadyPostfix)));
    }

    // The PC early-access popup ("press F2 for the feedback form") is not part of a mobile
    // release; NMainMenu._Ready only shows it while SeenEaDisclaimer is false.
    public static void MainMenuReadyPrefix()
    {
        try
        {
            if (ShowPortUi)
                return;
            var settings = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.SettingsSave;
            if (settings != null && !settings.SeenEaDisclaimer)
                settings.SeenEaDisclaimer = true;

            // PC Normal speed holds each played card mid-screen through its whole
            // animation, which reads as a stall on a phone. Default to the game's own
            // Fast mode once; the player's later choice in Settings > General wins.
            var prefs = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.PrefsSave;
            if (prefs != null && !AndroidSettingsBridge.GetBool(FastModeDefaultKey, false))
            {
                if (prefs.FastMode == MegaCrit.Sts2.Core.Settings.FastModeType.Normal)
                {
                    prefs.FastMode = MegaCrit.Sts2.Core.Settings.FastModeType.Fast;
                    MegaCrit.Sts2.Core.Saves.SaveManager.Instance.SavePrefsFile();
                }
                AndroidSettingsBridge.SetBool(FastModeDefaultKey, true);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Native early-access popup skip failed: {exception.Message}");
        }
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
            var fields = Traverse.Create(__instance);
            if (fields.Field("_moddedWarning").GetValue<Control>() is { } warning)
                warning.Visible = false;
            // In a run the PC build stamps "[version] (date)" and the run seed in the top
            // right for bug reports; STS1 mobile's combat/map screens show nothing there.
            if (!fields.Field("isMainMenu").GetValue<bool>())
            {
                if (fields.Field("_releaseInfo").GetValue<Control>() is { } release)
                    release.Visible = false;
                if (fields.Field("_seed").GetValue<Control>() is { } seed)
                    seed.Visible = false;
            }
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
