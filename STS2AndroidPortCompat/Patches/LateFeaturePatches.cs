using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Mobile.Patches;

/// <summary>
/// Feature patches that only matter inside a run (icon touch pads, the card-advisor
/// bridge) are installed once the main menu is up instead of in the startup patch pass.
/// Installing them during startup made Godot 4.5 on Android abort intermittently with
/// "BUG: Unreferenced static string to 0: _recognize_path" (the same failure the
/// upstream port saw with other early patches). By the main menu every patched type is
/// fully initialised by the game itself, and none of them exists before a run starts.
/// </summary>
public static class LateFeaturePatches
{
    private static Harmony _harmony;
    private static bool _scheduled;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(LateFeaturePatches), nameof(MainMenuReadyPostfix)));
    }

    public static void MainMenuReadyPostfix()
    {
        if (_scheduled || _harmony == null)
            return;
        _scheduled = true;
        try
        {
            Callable.From(ApplyNow).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Late feature patches could not be scheduled: {exception.Message}");
        }
    }

    private static void ApplyNow()
    {
        Run("Mobile touch targets", () => MobileTouchTargetPatches.Apply(_harmony));
        Run("Card advisor bridge", () => CardAdvisorBridge.Apply(_harmony));
        Run("Idle frame rate saver", () => IdleFrameRatePatches.Apply(_harmony));
        Run("Combat recorder", () => CombatRecorder.Apply(_harmony));
        Run("Combat state bridge", () => CombatStateBridge.Apply(_harmony));
        Run("Run advisor bridge", () => RunAdvisorBridge.Apply(_harmony));
#if STS2_LAB
        Run("Lab", () => AutoLab.Apply(_harmony));
#endif
    }

    private static void Run(string label, Action apply)
    {
        try
        {
            apply();
            PatchHelper.Log($"{label} applied (after main menu).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{label} failed: {exception}");
        }
    }
}
