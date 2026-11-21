using System;

namespace STS2Mobile.Patches;

// Engine-free sizing rules for the phone combat screen (hand card size, End Turn and
// energy orb size). Kept free of Godot types so the tests can run them directly.
// Idea credit: larger hand cards and bigger combat buttons on phones, as in
// Tap-the-Spire (CombatCardScale, MIT) and the iOS launcher's portrait HUD.
public static class MobileCombatSizing
{
    public const string HandCardScaleKey = "mobile_hand_card_scale_percent";
    public const int HandCardScaleDefault = 110;
    public const int HandCardScaleMin = 100;
    public const int HandCardScaleMax = 130;

    public const string CombatButtonScaleKey = "mobile_combat_button_scale_percent";
    public const int CombatButtonScaleDefault = 125;
    public const int CombatButtonScaleMin = 100;
    public const int CombatButtonScaleMax = 150;

    // Card edges keep at least this far from the screen side (container-local units).
    public const float HandEdgeMargin = 24f;

    public static float Factor(int percent, int min, int max) => Math.Clamp(percent, min, max) / 100f;

    // How far apart to spread the hand: as far as the cards grew, so they overlap no more
    // than in vanilla, but never past the screen side and never closer than vanilla.
    // halfLimit is the room from the hand's center to the nearer screen side; 0 = unknown.
    public static float HandSpread(float cardFactor, float maxAbsX, float scaledHalfCardWidth, float halfLimit)
    {
        if (cardFactor <= 1f)
            return 1f;
        if (maxAbsX <= 0.5f || halfLimit <= 0f)
            return cardFactor;
        var fits = (halfLimit - HandEdgeMargin - scaledHalfCardWidth) / maxAbsX;
        return Math.Clamp(fits, 1f, cardFactor);
    }
}
