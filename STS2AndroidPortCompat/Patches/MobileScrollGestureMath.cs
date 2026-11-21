using System;

namespace STS2Mobile.Patches;

/// <summary>
/// Engine-free drag/fling math for MobileGridTouchScrollPatches, kept
/// separate so it can be exercised by the plain .NET test runner.
/// Scroll values follow the game's convention: the target is the content's Y offset,
/// so dragging the finger up (negative delta) moves the target down.
/// </summary>
public static class MobileScrollGestureMath
{
    public static bool ExceedsSlop(float dx, float dy, float slop)
    {
        return dx * dx + dy * dy >= slop * slop;
    }

    /// <summary>Returns the target after a finger delta, damping movement that pushes further past a limit.</summary>
    public static float ApplyDrag(float target, float delta, float low, float high, float overscrollFactor)
    {
        if (low > high)
            (low, high) = (high, low);
        var next = target + delta;
        if (delta < 0f && next < low)
            return Math.Min(target, low) + (next - Math.Min(target, low)) * overscrollFactor;
        if (delta > 0f && next > high)
            return Math.Max(target, high) + (next - Math.Max(target, high)) * overscrollFactor;
        return next;
    }

    /// <summary>
    /// Advances a fling by <paramref name="dt"/> seconds. Returns false once the fling
    /// should stop: it slowed down, or the target left the scroll range (the game's own
    /// bounce-back takes over from there).
    /// </summary>
    public static bool StepFling(ref float target, ref float velocity, float dt, float friction, float low, float high, float stopVelocity)
    {
        if (low > high)
            (low, high) = (high, low);
        if (dt <= 0f)
            return Math.Abs(velocity) >= stopVelocity;
        target += velocity * dt;
        velocity *= (float)Math.Exp(-friction * dt);
        if (target <= low || target >= high)
            return false;
        return Math.Abs(velocity) >= stopVelocity;
    }

    public static float ClampMagnitude(float value, float max)
    {
        return Math.Max(-max, Math.Min(max, value));
    }

    /// <summary>Short ring of (time, cumulative offset) samples for release-velocity estimation.</summary>
    public sealed class VelocityTracker
    {
        private const int SampleCount = 8;
        private readonly ulong[] _times = new ulong[SampleCount];
        private readonly float[] _values = new float[SampleCount];
        private int _samples;
        private int _next;

        public ulong WindowUsec { get; set; } = 100_000;
        public ulong StaleUsec { get; set; } = 80_000;

        public void Add(ulong timeUsec, float cumulative)
        {
            _times[_next] = timeUsec;
            _values[_next] = cumulative;
            _next = (_next + 1) % SampleCount;
            if (_samples < SampleCount)
                _samples++;
        }

        /// <summary>Units per second over the recent window; 0 when the finger rested before lifting.</summary>
        public float Estimate(ulong nowUsec)
        {
            if (_samples < 2)
                return 0f;
            var newest = (_next - 1 + SampleCount) % SampleCount;
            var newestTime = _times[newest];
            if (nowUsec > newestTime && nowUsec - newestTime > StaleUsec)
                return 0f;
            var oldest = newest;
            for (var i = 1; i < _samples; i++)
            {
                var index = (newest - i + SampleCount) % SampleCount;
                if (newestTime - _times[index] > WindowUsec)
                    break;
                oldest = index;
            }
            var seconds = (newestTime - _times[oldest]) / 1_000_000f;
            return seconds < 0.008f ? 0f : (_values[newest] - _values[oldest]) / seconds;
        }

        public void Reset()
        {
            _samples = _next = 0;
        }
    }
}
