using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Patches;

/// <summary>
/// Phone games must survive being killed in the background. When Android pauses
/// the app, write the same files the game itself writes in NGame.Quit
/// (settings, prefs, progress, profile). The run file is already saved by the
/// game at room boundaries, so it is not touched here.
/// </summary>
public static class AndroidSaveOnPausePatches
{
    private const ulong MinIntervalMs = 2000;
    private static ulong _lastSaveMs;
    private static bool _installed;

    public static void Apply(Harmony harmony)
    {
        if (_installed)
            return;
        var notification = typeof(NGame).GetMethod("_Notification",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(int) }, null);
        if (notification == null)
            throw new MissingMethodException("NGame._Notification(int)");
        harmony.Patch(notification, postfix: new HarmonyMethod(
            PatchHelper.Method(typeof(AndroidSaveOnPausePatches), nameof(NotificationPostfix))));
        _installed = true;
    }

    public static void NotificationPostfix(int what)
    {
        if (what != (int)Node.NotificationApplicationPaused || OS.GetName() != "Android")
            return;
        var now = Time.GetTicksMsec();
        if (_lastSaveMs != 0 && now - _lastSaveMs < MinIntervalMs)
            return;
        _lastSaveMs = now;
        try
        {
            var saves = SaveManager.Instance;
            if (saves == null)
                return;
            saves.SaveSettings();
            if (saves.IsProfileInitialized)
            {
                saves.SavePrefsFile();
                saves.SaveProgressFile();
                saves.SaveProfile();
            }
            PatchHelper.Log("Saved settings/prefs/progress/profile on Android pause.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Save on Android pause failed: {exception.Message}");
        }
    }
}
