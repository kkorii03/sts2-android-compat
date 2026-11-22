using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

// Leave queue ownership, retries, cache writes, logging and completion in STS2.
// Yield only before removing the next item, never by pretending a queue is empty.
internal static class RuntimeAssetLoadingPatches
{
    private static readonly ConditionalWeakTable<AssetLoadingSession, Budget> Budgets = new();
    private static Harmony _harmony;
    private static bool _installed;
    private static readonly string[] Phases = { "FinalizeLoading", "ProcessLoadingQueue", "CheckLoadingStatus", "ProcessVfxQueue" };

    internal static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(RuntimeAssetLoadingPatches), nameof(GameReadyPostfix)));
    }

    private static void GameReadyPostfix()
    {
        if (_installed || OS.GetName() != "Android") return;
        _installed = true;
        try
        {
            var methods = Phases.Select(name => AccessTools.DeclaredMethod(typeof(AssetLoadingSession), name)
                ?? throw new MissingMethodException(typeof(AssetLoadingSession).FullName, name)).ToArray();
            foreach (var method in methods) Validate(method, PatchProcessor.GetOriginalInstructions(method));
            foreach (var method in methods)
                _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RuntimeAssetLoadingPatches), nameof(Transpiler)));
            _harmony.Patch(methods[3], prefix: new HarmonyMethod(typeof(RuntimeAssetLoadingPatches), nameof(VfxPrefix)));
            PatchHelper.Log("Runtime resource sessions budgeted: 2ms cooperative frame budget, at most 8 items per phase (12ms / 32 behind a room transition); original completion and failures retained.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Runtime resource session budget unavailable: {exception}");
        }
    }

    private static int FindDequeue(IList<CodeInstruction> code)
    {
        for (int i = 3; i < code.Count; i++)
            if (code[i].operand is MethodInfo method && method.DeclaringType == typeof(Queue<string>) && method.Name == "TryDequeue")
                return i;
        return -1;
    }

    private static void Validate(MethodBase method, IList<CodeInstruction> code)
    {
        int index = FindDequeue(code);
        if (method is not MethodInfo { ReturnType: var result } || result != typeof(void)
            || method.GetMethodBody().ExceptionHandlingClauses.Count != 0 || index < 3
            || code[index - 3].opcode != OpCodes.Ldarg_0 || code[index - 2].opcode != OpCodes.Ldfld
            || code[index - 1].opcode != OpCodes.Ldloca_S && code[index - 1].opcode != OpCodes.Ldloca)
            throw new InvalidOperationException($"Unknown resource queue loop shape: {method}");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
    {
        var code = instructions.ToList();
        Validate(__originalMethod, code);
        int before = FindDequeue(code) - 3;
        int phase = Array.IndexOf(Phases, __originalMethod.Name);
        var proceed = generator.DefineLabel();
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(code[before].labels);
        code[before].labels.Clear();
        code[before].labels.Add(proceed);
        code.InsertRange(before, new[]
        {
            first,
            new CodeInstruction(OpCodes.Ldc_I4, phase),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RuntimeAssetLoadingPatches), nameof(Allow))),
            new CodeInstruction(OpCodes.Brtrue, proceed),
            new CodeInstruction(OpCodes.Ret),
        });
        return code;
    }

    private static bool VfxPrefix(AssetLoadingSession __instance) => Allow(__instance, 3);
    private static bool Allow(AssetLoadingSession session, int phase) => Budgets.GetValue(session, static _ => new Budget()).Take(phase);

    // 2 ms/frame keeps gameplay smooth while assets stream in. Behind the black room
    // transition (NTransition.InTransition) nothing moves, so a frame hitch is invisible;
    // there the same queue may use 12 ms and 4x the items, which shortens the black
    // screen between map and room (S24U feedback: "2-3 s black").
    private const ulong VisibleBudgetUsec = 2000;
    private const ulong CoveredBudgetUsec = 12000;
    private const int CoveredItemMultiplier = 4;
    private static ulong _coveredFrame = ulong.MaxValue;
    private static bool _covered;

    // Boot (black screen / splash, nothing interactive) counts as covered until the first
    // main menu is ready: MainMenuEssentials took 1.3 s under the 2 ms budget on the S24U.
    private static bool _bootFinished;

    internal static void MarkBootFinished() => _bootFinished = true;

    private static bool IsScreenCovered(ulong frame)
    {
        if (frame == _coveredFrame)
            return _covered;
        _coveredFrame = frame;
        if (!_bootFinished)
        {
            _covered = true;
            return true;
        }
        try
        {
            var transition = NGame.Instance?.Transition;
            _covered = transition != null && GodotObject.IsInstanceValid(transition) && transition.InTransition;
        }
        catch
        {
            _covered = false;
        }
        return _covered;
    }

    private struct FrameBudget
    {
        private ulong _frame;
        private ulong _started;
        private int _items;
        private readonly int _maximumItems;

        internal FrameBudget(int maximumItems)
        {
            _frame = Engine.GetProcessFrames();
            _started = Time.GetTicksUsec();
            _items = 0;
            _maximumItems = maximumItems;
        }

        internal bool TryBeginItem(ulong frame, bool screenCovered)
        {
            ResetForFrame(frame);
            var maxItems = screenCovered ? _maximumItems * CoveredItemMultiplier : _maximumItems;
            var budgetUsec = screenCovered ? CoveredBudgetUsec : VisibleBudgetUsec;
            if (_items >= maxItems || Time.GetTicksUsec() - _started >= budgetUsec)
                return false;
            _items++;
            return true;
        }

        private void ResetForFrame(ulong frame)
        {
            if (frame == _frame) return;
            _frame = frame;
            _started = Time.GetTicksUsec();
            _items = 0;
        }
    }
    private sealed class Budget
    {
        private ulong _frame = ulong.MaxValue;
        private int _finalized, _requested, _checked, _vfx;
        private FrameBudget _time = new(24);

        internal bool Take(int phase)
        {
            ulong frame = Engine.GetProcessFrames();
            if (frame != _frame)
            {
                _frame = frame;
                _finalized = _requested = _checked = _vfx = 0;
            }
            // Separate item quotas let status checks run even when a large request
            // queue is present. All phases still share the same 2ms wall budget.
            ref int count = ref (phase == 0 ? ref _finalized : ref phase == 1 ? ref _requested : ref phase == 2 ? ref _checked : ref _vfx);
            var covered = IsScreenCovered(frame);
            if (count >= (covered ? 8 * CoveredItemMultiplier : 8) || !_time.TryBeginItem(frame, covered)) return false;
            count++;
            return true;
        }
    }
}
