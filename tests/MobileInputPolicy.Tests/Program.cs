using STS2Mobile.Patches;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var policy = new MobileInputPolicy();
Require(policy.ObserveTouch(0, true), "A finger must acquire the shared pointer.");
policy.ObserveHardwareKey();
Require(policy.UseTouchControls && !policy.RequestedTouchControls, "A keyboard shortcut must not change an in-flight touch release rule.");
Require(policy.ObserveTouch(0, false) && policy.UseTouchControls, "The final release must retain touch rules throughout viewport delivery.");
policy.FinishEvent();
Require(!policy.UseTouchControls && !policy.HasActiveGesture, "The pending keyboard policy must apply after the touch gesture ends.");

Require(policy.ObserveTouch(1, true), "A fresh finger must switch back without reconnecting peripherals.");
Require(policy.ObserveMouseButton(true, 1, true, false), "The engine's emulated mouse belongs to the same touch gesture.");
Require(!policy.ObserveMouseMotion(false, 6, 0), "A competing mouse must not move a finger's card or steal its hover target.");
Require(policy.ObserveTouch(1, false), "The touch contact must receive its own release.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The engine's matching emulated release is still part of this gesture.");
Require(policy.ObserveMouseButton(true, 1, false, false), "The emulated release must not be mistaken for a hardware release.");
Require(policy.UseTouchControls, "Release callbacks still need touch semantics.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "A real mouse request must survive touch emulation without mode oscillation.");

Require(policy.ObserveMouseButton(false, 1, true, false), "A real mouse drag must acquire the pointer.");
Require(!policy.ObserveTouch(2, true) && !policy.ObserveTouchDrag(2), "A competing finger cannot hijack a mouse drag.");
Require(!policy.ObserveMouseButton(true, 1, true, false), "The competing finger's emulated mouse press must also be swallowed.");
Require(policy.ObserveMouseButton(false, 1, false, false), "Mouse up must reach the original drag.");
Require(!policy.UseTouchControls, "The mouse release cannot be reinterpreted as a touch confirmation.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The finger's request takes effect only after the mouse drag ends.");
Require(!policy.ObserveTouch(2, false), "The residual finger release cannot click a newly switched UI.");
Require(!policy.ObserveMouseButton(true, 1, false, false), "The residual emulated release cannot complete an unrelated gesture.");

Require(policy.ObserveTouch(3, true), "A new touch gesture must work after the swallowed stream ends.");
Require(!policy.ObserveMouseButton(false, 3, true, false), "Middle-click cannot take over the touch gesture.");
Require(policy.ObserveTouch(3, false), "The original finger still releases normally.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "A pending real mouse button is a meaningful switch request.");
Require(!policy.ObserveMouseMotion(false, 20, 0), "A mouse button whose down was swallowed cannot drag after the owner ends.");
Require(!policy.ObserveMouseButton(false, 3, false, false), "Its unmatched up must remain swallowed.");
Require(policy.ObserveMouseButton(false, 3, true, false), "The next complete middle-click must work.");
Require(policy.ObserveMouseButton(false, 3, false, false), "Its matching release must work.");
policy.FinishEvent();

Require(policy.ObserveTouch(4, true) && policy.ObserveTouch(5, true), "Multiple fingers share touch ownership.");
policy.ObserveHardwareKey();
Require(policy.ObserveTouch(4, false), "The first finger can leave.");
policy.FinishEvent();
Require(policy.UseTouchControls, "A remaining finger must keep the touch gesture locked.");
Require(policy.ObserveTouch(5, false), "The last finger can leave.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "All fingers must be up before a pending keyboard switch commits.");

Require(policy.ObserveTouch(6, true) && policy.ObserveTouch(6, false), "Touch can reacquire the pointer.");
policy.FinishEvent();
for (var i = 0; i < 100; i++)
{
    Require(!policy.ObserveMouseMotion(false, 1, 0), "Subthreshold mouse jitter must not steal touch mode.");
    Require(!policy.ObserveMouseMotion(false, -1, 0), "Opposite jitter must not accumulate absolute travel forever.");
}
Require(policy.UseTouchControls, "Stationary pointer jitter must retain touch policy.");
Require(policy.ObserveMouseMotion(false, 4, 0) && !policy.UseTouchControls, "A deliberate mouse movement must switch immediately at the threshold.");
Require(policy.ObserveTouch(7, true) && policy.ObserveTouch(7, false), "Touch must remain available after moving the mouse.");
policy.FinishEvent();
Require(!policy.ObserveMouseMotion(false, 1000, 0) && policy.UseTouchControls, "A pointer warp must not masquerade as deliberate hardware movement.");
Require(policy.ObserveMouseButton(false, 4, true, true) && !policy.UseTouchControls, "A wheel tick must switch without requiring a click first.");
Require(!policy.HasActiveGesture, "Wheel ticks cannot leave a pointer lease held.");

Require(policy.ObserveMouseButton(false, 1, true, false), "A mouse drag starts.");
Require(policy.ObserveMouseButton(false, 2, true, false), "A second real mouse button can join the same gesture.");
Require(!policy.ObserveTouch(8, true), "Touch cannot steal a multibutton drag.");
Require(policy.ObserveMouseButton(false, 1, false, false), "The first real button releases.");
policy.FinishEvent();
Require(!policy.UseTouchControls && policy.HasActiveGesture, "A second held button must keep ownership.");
Require(policy.ObserveMouseButton(false, 2, false, false), "The final real button releases.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The pending touch switch commits after all real buttons release.");
policy.Reset();
Require(!policy.ObserveTouch(8, false), "A stale release after pause/reset cannot click anything.");
Require(policy.ObserveMouseButton(false, 1, true, false), "A fresh mouse press must work after pause/reset.");
Require(!policy.UseTouchControls, "Reset cannot strand the policy behind stale touch ownership.");

Console.WriteLine("PASS: automatic input switching, complete touch/emulated/mouse gesture ownership, competing stream suppression, movement thresholds and lifecycle reset.");

// Grid/list touch scrolling (MobileGridTouchScrollPatches): slop, overscroll damping, release velocity, fling.
Require(!MobileScrollGestureMath.ExceedsSlop(10, 20, 24), "A 22-unit wobble during a tap must stay a tap.");
Require(MobileScrollGestureMath.ExceedsSlop(0, -24, 24), "Reaching the slop starts a drag.");
Require(MobileScrollGestureMath.ExceedsSlop(30, 0, 24), "A sideways drag past the slop is still a drag and must never click on release.");

Require(MobileScrollGestureMath.ApplyDrag(-100, -50, -1000, 0, 0.4f) == -150, "Inside the range the content follows the finger 1:1.");
Require(MobileScrollGestureMath.ApplyDrag(0, 10, -1000, 0, 0.4f) == 4, "Pulling past the top is damped.");
Require(MobileScrollGestureMath.ApplyDrag(-990, -20, -1000, 0, 0.4f) == -1004, "Only the part beyond the bottom limit is damped.");
Require(MobileScrollGestureMath.ApplyDrag(8, -10, -1000, 0, 0.4f) == -2, "Pushing back from overscroll is not damped.");
Require(MobileScrollGestureMath.ApplyDrag(-100, 10, 0, -1000, 0.4f) == -90, "Limits may arrive in either order.");

var tracker = new MobileScrollGestureMath.VelocityTracker();
Require(tracker.Estimate(0) == 0f, "No samples means no fling.");
for (ulong t = 0; t <= 80_000; t += 16_000)
    tracker.Add(1_000_000 + t, -(t / 1_000_000f) * 2000f);
var velocity = tracker.Estimate(1_080_000);
Require(Math.Abs(velocity + 2000f) < 1f, $"A steady 2000/s upward swipe must be measured as such, got {velocity}.");
Require(tracker.Estimate(1_080_000 + 200_000) == 0f, "Resting the finger before lifting cancels the fling.");
tracker.Reset();
tracker.Add(5_000_000, 0f);
tracker.Add(5_002_000, 500f);
Require(tracker.Estimate(5_002_000) == 0f, "Two samples 2 ms apart are noise, not a fling.");

float flingTarget = -500f, flingVelocity = -3000f;
var steps = 0;
while (MobileScrollGestureMath.StepFling(ref flingTarget, ref flingVelocity, 1f / 60f, 4f, -100_000f, 0f, 25f))
    steps++;
Require(steps > 30 && steps < 200 && Math.Abs(flingVelocity) < 25f, $"A fling decays to a stop in a sensible time ({steps} frames).");
Require(flingTarget < -1000f && flingTarget > -1350f, $"Fling travel is roughly v/friction ({flingTarget}).");
flingTarget = -50f; flingVelocity = 3000f;
Require(!MobileScrollGestureMath.StepFling(ref flingTarget, ref flingVelocity, 1f / 30f, 4f, -1000f, 0f, 25f) && flingTarget > 0f,
    "A fling that crosses the top limit stops and leaves the overscroll to the game's bounce-back.");
Require(MobileScrollGestureMath.ClampMagnitude(-9000f, 6000f) == -6000f, "Fling speed is capped.");
Console.WriteLine("PASS: grid touch scroll slop, overscroll damping, release velocity and fling decay.");

// Leftover-hover clear after a touch tap (MobileHoverReleasePatches).
var hover = new MobileHoverReleaseTracker();
hover.MousePressed(100, 100);
hover.TouchPressed(0);
hover.Moved(110, 120);
var tapSerial = hover.Serial;
Require(hover.MouseReleased(false), "A still single-finger tap clears its leftover hover.");
hover.TouchReleased(0);
hover.MousePressed(100, 100);
Require(hover.Serial != tapSerial, "A new press invalidates a clear queued for the previous tap.");
hover.TouchPressed(0);
hover.Moved(100, 300);
hover.Moved(100, 100);
Require(!hover.MouseReleased(false), "A drag (map, grid, hand) that comes back to its start is still not a tap.");
hover.TouchReleased(0);
hover.MousePressed(100, 100);
hover.TouchPressed(0);
hover.TouchPressed(1);
hover.TouchReleased(1);
Require(!hover.MouseReleased(false), "A two-finger gesture never clears hover, even after the second finger lifts.");
hover.TouchReleased(0);
hover.MousePressed(100, 100);
hover.TouchPressed(0);
Require(!hover.MouseReleased(true), "A cancelled touch (system gesture, palm) is not a tap.");
hover.TouchReleased(0);
Require(!hover.MouseReleased(false), "A release without its press is not a tap.");
hover.TouchPressed(5);
hover.MousePressed(10, 10);
hover.TouchPressed(0);
Require(hover.MouseReleased(false), "A finger whose release was lost cannot block every later tap.");
hover.MousePressed(10, 10);
hover.Reset();
Require(!hover.MouseReleased(false), "Input-mode reset drops the gesture in flight.");

string[] creature = { "Control", "NCreature", "Node2D" };
Require(MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, creature), "Tapping the player clears its nameplate hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, null!), "Nothing hovered: no synthetic pointer event.");
Require(!MobileHoverReleasePolicy.ShouldClear(false, false, false, true, false, false, creature), "Mouse/keyboard mode keeps real hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, true, false, true, false, false, creature), "Immediate tooltip mode reads tips through hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, true, true, false, false, creature), "A finger back down keeps its hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, false, false, false, creature), "A newer gesture owns the pointer.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, true, false, creature), "Targeting/card play follows the pointer.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, true, creature), "A tap-pinned icon tooltip would be removed by its owner's MouseExited.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, new[] { "NClickableControl", "NHandCardHolder", "Control" }),
    "A tapped (pinned) hand card keeps its hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, new[] { "NClickableControl", "NGridCardHolder" }),
    "Reward/grid selection previews keep their hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, new[] { "TextureRect", "NMapLegendItem", "VBoxContainer" }),
    "The map legend highlight lives on hover.");
Require(!MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, new[] { "Control", "NMerchantRelic" }),
    "A shop slot's purchase preview keeps its hover.");
Require(MobileHoverReleasePolicy.ShouldClear(true, false, false, true, false, false, new[] { "Control", "NPotionHolder" }),
    "A tapped potion (popup stays open) drops its hover glow.");
Console.WriteLine("PASS: touch tap hover release tracking and exclusions.");
