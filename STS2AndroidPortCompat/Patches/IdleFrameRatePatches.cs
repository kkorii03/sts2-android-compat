using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Battery saver for the 120 Hz mode: after <see cref="IdleMs"/> without any input the game
/// renders at <see cref="IdleFps"/> (STS1's rate); the next touch restores the full rate in the
/// same frame, so scrolling, dragging and aiming stay at 120. Only acts while the configured
/// limit is above the idle rate, and only on Android. Setting mobile_idle_fps_saver (default on).
/// </summary>
public static class IdleFrameRatePatches
{
    public const string SettingKey = "mobile_idle_fps_saver";
    private const int IdleFps = 60;
    private const ulong IdleMs = 2500;

    private static ulong _lastInputMs;
    private static bool _capped;
    private static int _fullFps;
    private static bool _hooked;
    private static int _enabled; // 0 unknown, 1 on, 2 off (read once per session)

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid)
            return;
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", prefix: PatchHelper.Method(typeof(IdleFrameRatePatches), nameof(InputPrefix)));
        _lastInputMs = Time.GetTicksMsec();
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null && !_hooked)
        {
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(OnFrame));
            _hooked = true;
        }
    }

    private static bool Enabled
    {
        get
        {
            if (_enabled == 0)
                _enabled = AndroidSettingsBridge.GetBool(SettingKey, true) ? 1 : 2;
            return _enabled == 1;
        }
    }

    public static void InputPrefix()
    {
        _lastInputMs = Time.GetTicksMsec();
        if (_capped)
            Restore();
    }

    private static void OnFrame()
    {
        try
        {
            if (!_capped)
            {
                _fullFps = Engine.MaxFps;
                // 0 = unlimited; anything at or under the idle rate needs no saving.
                if (!Enabled || (_fullFps > 0 && _fullFps <= IdleFps))
                    return;
                if (Time.GetTicksMsec() - _lastInputMs >= IdleMs)
                {
                    Engine.MaxFps = IdleFps;
                    _capped = true;
                }
            }
            else if (Engine.MaxFps != IdleFps)
            {
                // Someone (settings screen) changed the limit while idle: take it as the new full rate.
                _fullFps = Engine.MaxFps;
                _capped = false;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Idle frame rate failed: {exception.Message}");
            _enabled = 2;
        }
    }

    private static void Restore()
    {
        _capped = false;
        Engine.MaxFps = _fullFps;
    }
}
