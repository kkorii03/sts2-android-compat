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
    private const string HighRefreshDefaultKey = "mobile_120hz_default_applied";
    private const string RenderDefaultKey = "mobile_render_fhd_default_applied";

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

        PatchHelper.Patch(harmony, typeof(NTransition), "RoomFadeOut", prefix: PatchHelper.Method(typeof(NativeFeel), nameof(RoomFadeOutPrefix)));
        PatchHelper.Patch(harmony, typeof(NTransition), "FadeIn", prefix: PatchHelper.Method(typeof(NativeFeel), nameof(TransitionFadeInPrefix)));
        try
        {
            var launch = AccessTools.Method(typeof(NGame), "LaunchMainMenu", new[] { typeof(bool) });
            if (launch != null)
                harmony.Patch(launch, prefix: new HarmonyMethod(PatchHelper.Method(typeof(NativeFeel), nameof(LaunchMainMenuLogoPrefix))) { priority = Priority.First });
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED intro logo patch: {exception.Message}");
        }
        try
        {
            var locAdd = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "Add", new[] { typeof(string), typeof(string) });
            if (locAdd != null)
            {
                harmony.Patch(locAdd, prefix: new HarmonyMethod(PatchHelper.Method(typeof(NativeFeel), nameof(LocAddPrefix))));
                PatchHelper.Log("Patched LocString.Add(string, string) for touch hotkey hints");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED LocString.Add hotkey hint patch: {exception.Message}");
        }
        PatchHelper.Patch(harmony, typeof(NOpenProfileScreenButton), "RefreshLabels", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(ProfileLabelsPostfix)));
        PatchHelper.Patch(harmony, typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapShareButton), "Initialize", postfix: PatchHelper.Method(typeof(NativeFeel), nameof(MapShareInitializePostfix)));

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

            // The S24 Ultra panel does 120 Hz; the first builds defaulted to 60 Hz plus the
            // PC 60 fps cap. Move once to 120 Hz + 120 fps (user request); "Battery saver
            // (60 Hz)" in the Mobile tab still switches both back to 60.
            if (!AndroidSettingsBridge.GetBool(HighRefreshDefaultKey, false))
            {
                AndroidSettingsBridge.SetString("android_display_refresh_rate_mode", "high");
                AndroidInGameSettingsPatches.ApplyDisplayRefreshRateMode("high");
                AndroidInGameSettingsPatches.SetGameFpsLimit(120);
                AndroidSettingsBridge.SetBool(HighRefreshDefaultKey, true);
            }
            else if (string.Equals(AndroidSettingsBridge.GetString("android_display_refresh_rate_mode", "high"), "high", StringComparison.OrdinalIgnoreCase)
                && Engine.MaxFps != 120)
            {
                Engine.MaxFps = 120;
            }

            // Heat (user report): native QHD+ 3120x1440 is 4.5 MP per frame. Once, move an
            // untouched "native" render size to the 1920x1080 preset, which on a 19.5:9 phone
            // renders FHD+ (2340x1080, -44% pixels; the phone's own default). The Mobile tab's
            // "Render resolution" still offers native.
            if (!AndroidSettingsBridge.GetBool(RenderDefaultKey, false))
            {
                var current = AndroidSettingsBridge.GetSize("fullscreen_render_size");
                if (current.X <= 0 || current.Y <= 0)
                {
                    AndroidSettingsBridge.SetSize("fullscreen_render_size", 1920, 1080);
                    DisplaySettingsPatches.ApplyRuntimeDisplaySettings();
                }
                AndroidSettingsBridge.SetBool(RenderDefaultKey, true);
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
        RuntimeAssetLoadingPatches.MarkBootFinished();
        HideBootCover();
        try
        {
            if (ShowPortUi)
                return;
            HideButton(Traverse.Create(__instance).Field("_quitButton").GetValue<Control>());
            HideButton(Traverse.Create(__instance).Field("_patchNotesButtonNode").GetValue<Control>());
            HideButton(Traverse.Create(__instance).Field("_multiplayerButton").GetValue<Control>());
            SpaceMainMenuButtons(__instance);
            // Button sizes are still zero during _Ready; retry once layout has run.
            var menu = __instance;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(menu))
                    SpaceMainMenuButtons(menu);
            }).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Native main-menu cleanup failed: {exception.Message}");
        }
    }

    // The Java GodotApp keeps the game icon over the engine's black boot surface (~8 s on
    // the S24U). The first NTransition.FadeIn is the game revealing its own logo (or the
    // menu when the logo is skipped): drop the cover then; the main menu is a fallback.
    private static bool _bootCoverHidden;

    public static void TransitionFadeInPrefix() => HideBootCover();

    // STS1 mobile: studio logo ~2 s after the tap, held while loading, then the menu. When
    // the boot cover already shows the game's logo (assets/boot_logo.png in the personal
    // APK) for the whole engine boot, playing the ~9 s logo animation again would double
    // it; go to the menu instead. Without that image the vanilla logo choice stands.
    public static void LaunchMainMenuLogoPrefix(ref bool __0)
    {
        try
        {
            if (__0 || ShowPortUi || !IsAndroid)
                return;
            var wrapper = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
            if (wrapper.Call("hasBootLogoFromGame").AsBool())
            {
                __0 = true;
                PatchHelper.Log("Boot cover shows the game logo; skipping the repeated logo animation.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Boot logo check failed; keeping the logo animation: {exception.Message}");
        }
    }

    private static void HideBootCover()
    {
        if (_bootCoverHidden || !IsAndroid)
            return;
        _bootCoverHidden = true;
        try
        {
            var wrapper = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
            wrapper.Call("hideBootCoverFromGame");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Boot cover hide bridge failed: {exception.Message}");
        }
    }

    // Hover-tip titles append the PC key in brackets ("End Turn (E)", "Deck (D)", "Map (M)")
    // through {Hotkey:choose(None):| ({})}. With touch controls there is no key to press;
    // "None" is the game's own value for "no binding", which drops the bracket.
    private static readonly string HotkeyVar = "Hotkey";

    public static void LocAddPrefix(string __0, ref string __1)
    {
        if (__0 != HotkeyVar)
            return;
        try
        {
            if (IsAndroid && MobileInputPolicyPatches.UseTouchControls)
                __1 = "None";
        }
        catch
        {
        }
    }

    // STS1 mobile's profile caption says "tap", not the PC "click to edit".
    public static void ProfileLabelsPostfix(NOpenProfileScreenButton __instance)
    {
        try
        {
            if (!IsAndroid || ShowPortUi)
                return;
            if (Traverse.Create(__instance).Field("_description").GetValue<MegaCrit.Sts2.addons.mega_text.MegaLabel>() is { } description)
                description.SetTextAutoSize(ModText.Ko("Tap to edit", "탭하여 편집"));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Profile caption override failed: {exception.Message}");
        }
    }

    // The map "share" button saves a PNG and opens the PC file manager; nothing on a phone.
    public static void MapShareInitializePostfix(Control __instance)
    {
        if (!ShowPortUi)
            HideButton(__instance);
    }

    // Fast mode's room fade-out waits 0.3 s and then fades for 0.3 s before the next room
    // even starts loading. On the phone that idle wait reads as part of the "black screen";
    // fade straight away in 0.2 s instead. Normal/Instant keep vanilla.
    private const double FastRoomFadeOutSeconds = 0.2;
    private static AccessTools.FieldRef<NTransition, Control> SimpleTransitionRef;
    private static AccessTools.FieldRef<NTransition, Control> GradientTransitionRef;
    private static AccessTools.FieldRef<NTransition, float> InitialGradientYRef;
    private static AccessTools.FieldRef<NTransition, Tween> TweenRef;
    private static System.Reflection.MethodInfo InTransitionSetter;
    private static bool? _fastFadeReady;

    private static bool FastFadeReady()
    {
        if (_fastFadeReady.HasValue)
            return _fastFadeReady.Value;
        try
        {
            SimpleTransitionRef = AccessTools.FieldRefAccess<NTransition, Control>("_simpleTransition");
            GradientTransitionRef = AccessTools.FieldRefAccess<NTransition, Control>("_gradientTransition");
            InitialGradientYRef = AccessTools.FieldRefAccess<NTransition, float>("_initialGradientYPosition");
            TweenRef = AccessTools.FieldRefAccess<NTransition, Tween>("_tween");
            InTransitionSetter = AccessTools.PropertySetter(typeof(NTransition), "InTransition");
            _fastFadeReady = InTransitionSetter != null;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Fast room fade-out members missing, using vanilla: {exception.Message}");
            _fastFadeReady = false;
        }
        return _fastFadeReady.Value;
    }

    public static bool RoomFadeOutPrefix(NTransition __instance, ref System.Threading.Tasks.Task __result)
    {
        try
        {
            if (!IsAndroid || !FastFadeReady()
                || MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.PrefsSave?.FastMode != MegaCrit.Sts2.Core.Settings.FastModeType.Fast)
                return true;
            __result = FastRoomFadeOut(__instance);
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Fast room fade-out unavailable, using vanilla: {exception.Message}");
            return true;
        }
    }

    private static async System.Threading.Tasks.Task FastRoomFadeOut(NTransition transition)
    {
        InTransitionSetter.Invoke(transition, new object[] { true });
        var simple = SimpleTransitionRef(transition);
        var gradient = GradientTransitionRef(transition);
        var modulate = simple.Modulate;
        modulate.A = 0f;
        simple.Modulate = modulate;
        modulate = gradient.Modulate;
        modulate.A = 1f;
        gradient.Modulate = modulate;
        var position = gradient.Position;
        position.Y = InitialGradientYRef(transition);
        gradient.Position = position;
        TweenRef(transition)?.Kill();
        var tween = transition.CreateTween().SetParallel();
        TweenRef(transition) = tween;
        tween.TweenProperty(simple, "modulate:a", 1f, FastRoomFadeOutSeconds)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        if (await MegaCrit.Sts2.Core.Nodes.GodotExtensions.TweenHelper.AwaitFinished(tween, transition))
            transition.MouseFilter = Control.MouseFilterEnum.Stop;
    }

    // STS1 mobile's main menu items sit ~9.2% of the screen height apart (centre to centre);
    // the PC list is packed at ~5%, which is too tight for a thumb (S24U feedback). Widen the
    // VBox separation to that pitch and keep the list's bottom on screen.
    private const float MenuPitchRatio = 0.092f;
    private const float MenuBottomLimitRatio = 0.95f;
    private static readonly StringName SeparationName = "separation";

    private static void SpaceMainMenuButtons(NMainMenu menu)
    {
        try
        {
            if (menu.GetNodeOrNull<BoxContainer>("MainMenuTextButtons") is not { } list)
                return;
            float buttonHeight = 0f;
            foreach (var child in list.GetChildren())
            {
                if (child is Control { Visible: true } control)
                    buttonHeight = Math.Max(buttonHeight, control.Size.Y);
            }
            if (buttonHeight <= 0f)
                return;
            var visible = menu.GetViewportRect().Size;
            var separation = Mathf.RoundToInt(visible.Y * MenuPitchRatio - buttonHeight);
            if (separation > list.GetThemeConstant(SeparationName))
                list.AddThemeConstantOverride(SeparationName, separation);
            // STS1 mobile: a left-aligned text column (sts1-mobile-ui.md 15), each item
            // hugging its text instead of a centred column under the logo.
            foreach (var child in list.GetChildren())
            {
                if (child is Control control && control.SizeFlagsHorizontal != Control.SizeFlags.ShrinkBegin)
                    control.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                // The buttons keep a common minimum width with the text centred inside;
                // STS1 lines every item up on its left edge.
                // The label is a fit-to-text box anchored in the button centre, so stretch it
                // across the button width and left-align the text inside.
                if (child is Node button && button.GetChildCount() > 0 && button.GetChild(0) is Label label
                    && (label.HorizontalAlignment != HorizontalAlignment.Left || label.AnchorLeft != 0f || label.AnchorRight != 1f))
                {
                    label.AnchorLeft = 0f;
                    label.AnchorRight = 1f;
                    label.OffsetLeft = 0f;
                    label.OffsetRight = 0f;
                    label.HorizontalAlignment = HorizontalAlignment.Left;
                }
            }
            Callable.From(() => PlaceMenuLikeSts1(list)).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Main-menu spacing failed: {exception.Message}");
        }
    }

    private const float MenuLeftRatio = 0.05f;
    private const float MenuTopRatio = 0.53f;

    private static void PlaceMenuLikeSts1(Control list)
    {
        if (!GodotObject.IsInstanceValid(list) || !list.IsInsideTree())
            return;
        var visible = list.GetViewportRect().Size;
        var target = new Vector2(visible.X * MenuLeftRatio, visible.Y * MenuTopRatio);
        var rect = list.GetGlobalRect();
        var limit = visible.Y * MenuBottomLimitRatio;
        if (target.Y + rect.Size.Y > limit)
            target.Y = Math.Max(0f, limit - rect.Size.Y);
        if (!rect.Position.IsEqualApprox(target))
            list.GlobalPosition = target;
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

    private static readonly System.Collections.Generic.HashSet<string> HiddenSettingsRows = new(StringComparer.Ordinal)
    {
        "NShowMpMapDrawingsTickbox",
        "NHandCardCountTickbox",
        "NBackgroundModeTickbox",
    };

    public static void SettingsScreenReadyPostfix(Node __instance)
    {
        try
        {
            if (ShowPortUi)
                return;
            HideButton(__instance.GetNodeOrNull<Control>("%Modding"));
            HideButton(__instance.GetNodeOrNull<Control>("%ModdingDivider"));
            HideFeedbackRow(__instance);
            // STS1 mobile has no key-binding page; the Input tab only lists PC keyboard and
            // controller bindings. A connected keyboard/pad flips UseTouchControls off.
            // The Graphics tab is PC window/monitor/VSync/MSAA options; the phone-relevant
            // ones (font size, 60 Hz) live in the Mobile tab. STS1 mobile has neither tab.
            var tabs = __instance.GetNodeOrNull<Node>("%SettingsTabManager");
            if (MobileInputPolicyPatches.UseTouchControls && tabs != null)
            {
                HideButton(tabs.GetNodeOrNull<Control>("Input"));
                HideButton(tabs.GetNodeOrNull<Control>("Graphics"));
            }
            // PC-only General rows: multiplayer map drawings (single-player build), card
            // index numbers (keyboard 1-9 card hotkeys) and background FPS limit (Android
            // already pauses the game in the background).
            foreach (var node in __instance.FindChildren("*", "", true, false))
            {
                if (node is Control control && HiddenSettingsRows.Contains(control.GetType().Name))
                    HideSettingsRow(control);
            }
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
        HideSettingsRow(settingsScreen.GetNodeOrNull<Control>("%FeedbackButton"));
    }

    private static void HideSettingsRow(Control button)
    {
        if (button == null)
            return;
        var line = button.GetParent() as Control;
        var list = line?.GetParent();
        if (line != null && list is BoxContainer && line is not ScrollContainer)
        {
            HideButton(line);
            if (list.GetNodeOrNull<Control>(line.Name + "Divider") is { } named)
            {
                HideButton(named);
            }
            else
            {
                // Rows are separated by divider nodes whose names do not always follow
                // "<Row>Divider"; hide the divider right after the row so no doubled line
                // is left behind (seen on the S24U General tab).
                var index = line.GetIndex();
                if (index + 1 < list.GetChildCount() && list.GetChild(index + 1) is Control next
                    && next.Name.ToString().Contains("Divider", StringComparison.OrdinalIgnoreCase))
                    HideButton(next);
            }
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
