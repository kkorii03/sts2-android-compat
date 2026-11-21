using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace STS2Mobile.Patches;

// Test-only stand-in: confirm-button placement is purely visual and needs the real
// Godot layout, so the synthetic hot-path tests compile against this no-op.
internal static class MobileConfirmPlacement
{
    internal static void ShowNear(NConfirmButton button, Control anchor) { }

    internal static void Hide(NConfirmButton button) { }
}
