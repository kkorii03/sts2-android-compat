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

    // 100 = vanilla. Measured on the S24 Ultra (global_scale 1.1): vanilla End Turn
    // (~0.10 W x 0.09 H) and energy orb (~0.13 H) are already a bit bigger than the
    // STS1 mobile app's (0.09 W x 0.07 H, 0.12 H), and 125% let a 10-card hand's
    // right edge run under End Turn. Still adjustable up to 150% in settings.
    public const string CombatButtonScaleKey = "mobile_combat_button_scale_percent";
    public const int CombatButtonScaleDefault = 100;
    public const int CombatButtonScaleMin = 100;
    public const int CombatButtonScaleMax = 150;

    // Card edges keep at least this far from the screen side (container-local units).
    public const float HandEdgeMargin = 24f;

    // Vanilla hand card height (NCard default size), used when a hitbox has no size yet.
    public const float CardHeight = 422f;

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

    // How far down to move an enlarged resting card so it grows downward (more of it
    // below the screen edge, as in STS1 mobile) instead of upward into the battlefield
    // and the tap-to-play zone: its top edge stays where vanilla puts it.
    public static float GrowDownOffset(float cardHeight, float baseScaleY, float cardFactor)
    {
        if (cardFactor <= 1f)
            return 0f;
        if (cardHeight <= 1f)
            cardHeight = CardHeight;
        return cardHeight * baseScaleY * (cardFactor - 1f) * 0.5f;
    }
}
