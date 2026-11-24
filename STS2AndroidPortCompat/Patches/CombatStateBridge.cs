using System;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Sends what the player can see in a single-player combat to the separate personal app
/// com.kkorii.sts2combat (explicit broadcast, no hidden draw order, no RNG). Nothing is shown or
/// computed in the game. Setting combat_state_bridge (default on).
/// </summary>
public static class CombatStateBridge
{
    public const string SettingKey = "combat_state_bridge";
    private const double SettleSeconds = 0.35;

    private static GodotObject _java;
    private static int _generation;
    private static bool _failed;

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid)
            return;
        var self = typeof(CombatStateBridge);
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPlayerTurnStart), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCardPlayed), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPotionUsed), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforeSideTurnEnd), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCombatEnd), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
    }

    public static void RefreshPrefix(ICombatState combatState)
    {
        try
        {
            if (_failed || combatState == null || !AndroidSettingsBridge.GetBool(SettingKey, true))
                return;
            var generation = ++_generation;
            // Wait for the play to finish moving cards, then send the latest state once.
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.CreateTimer(SettleSeconds).Timeout += () =>
                {
                    if (generation != _generation)
                        return;
                    try
                    {
                        var snapshot = CombatRecorder.Snapshot("combat", combatState, null, null);
                        if (snapshot != null)
                            Deliver(Encoding.UTF8.GetString(snapshot));
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                    }
                };
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    public static void HidePrefix()
    {
        try
        {
            _generation++;
            if (!_failed)
                Deliver("{\"event\":\"hide\"}");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static void Deliver(string json)
    {
        if (_java == null || !GodotObject.IsInstanceValid(_java))
            _java = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
        _java.Call("sendCombatState", json);
    }

    private static void Fail(Exception exception)
    {
        _failed = true; // this session only; the game goes on
        PatchHelper.Log($"[CombatBridge] stopped: {exception}");
    }
}