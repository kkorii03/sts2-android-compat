using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Optional on-screen text for a turn advisor that is compiled in only for personal builds
/// (STS2_COMBAT_SIM in the project file). Without it this class does nothing. The label ignores
/// touches; the advisor runs on a worker thread from a snapshot of what the player can see.
/// Setting combat_advice (default on). Single player only.
/// </summary>
public static class CombatAdviceOverlay
{
    public const string SettingKey = "combat_advice";
    private const double SettleSeconds = 0.35;

    private static MethodInfo _advise;
    private static CanvasLayer _layer;
    private static Label _label;
    private static int _generation;
    private static bool _failed;

    public static bool Available => _advise != null;

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid)
            return;
        _advise = Type.GetType("CombatSim.Phone.TurnAdvisor")?.GetMethod("Advise", BindingFlags.Public | BindingFlags.Static);
        if (_advise == null)
        {
            PatchHelper.Log("[CombatAdvice] advisor not in this build.");
            return;
        }
        var self = typeof(CombatAdviceOverlay);
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
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.CreateTimer(SettleSeconds).Timeout += () => Compute(combatState, generation);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    public static void HidePrefix()
    {
        _generation++;
        try
        {
            Callable.From(() => { if (_label != null && GodotObject.IsInstanceValid(_label)) _label.Visible = false; }).CallDeferred();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static void Compute(ICombatState combat, int generation)
    {
        try
        {
            if (generation != _generation)
                return;
            var snapshot = CombatRecorder.Snapshot("advice", combat, null, null);
            if (snapshot == null)
                return;
            Task.Run(() =>
            {
                string text;
                var watch = Stopwatch.StartNew();
                try
                {
                    text = _advise.Invoke(null, new object[] { Encoding.UTF8.GetString(snapshot), 700 }) as string;
                }
                catch (Exception exception)
                {
                    text = "조언 계산 실패: " + (exception.InnerException ?? exception).Message;
                }
                PatchHelper.Log($"[CombatAdvice] {watch.ElapsedMilliseconds} ms");
                Callable.From(() => Show(text, generation)).CallDeferred();
            });
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static void Show(string text, int generation)
    {
        try
        {
            if (generation != _generation || string.IsNullOrEmpty(text) || Engine.GetMainLoop() is not SceneTree tree)
                return;
            if (_label == null || !GodotObject.IsInstanceValid(_label))
            {
                _layer = new CanvasLayer { Layer = 50, Name = "CombatAdvice" };
                _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                var box = new StyleBoxFlat { BgColor = new Color(0.04f, 0.07f, 0.1f, 0.72f) };
                box.SetCornerRadiusAll(10);
                box.SetContentMarginAll(10);
                _label.AddThemeStyleboxOverride("normal", box);
                _label.AddThemeFontSizeOverride("font_size", 22);
                _layer.AddChild(_label);
                tree.Root.AddChild(_layer);
            }
            var view = tree.Root.GetVisibleRect().Size;
            _label.Text = text;
            _label.Position = new Vector2(view.X * 0.015f, view.Y * 0.17f);
            _label.CustomMinimumSize = new Vector2(view.X * 0.24f, 0);
            _label.Size = new Vector2(view.X * 0.24f, 0);
            _label.Visible = true;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static void Fail(Exception exception)
    {
        _failed = true; // this session only; the game goes on
        PatchHelper.Log($"[CombatAdvice] stopped: {exception}");
    }
}
