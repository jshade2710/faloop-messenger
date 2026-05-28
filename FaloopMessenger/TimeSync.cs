using System;

namespace FaloopMessenger;

// Tracks the offset between the local PC clock and Faloop's server clock so
// "X seconds ago" displays match what the Faloop website shows. We feed this
// from the HTTP `Date` header on responses from faloop.app (no extra calls
// needed — we already hit /api/auth/user/refresh on every connect).
internal static class TimeSync
{
    // m-8 (v0.4.7 audit): _offset is written by the websocket/HTTP thread
    // (RecordServerTime, called from FetchAnonSession and TimeSyncLoop) and
    // read every frame by the render thread (ServerNow). Stored as ticks
    // (long) so we can use Interlocked.Read/Exchange for atomic access on
    // both x86 and x64; the public TimeSpan/DateTime properties wrap the
    // tick reads.
    private static long _offsetTicks;   // local − server, in ticks

    /// <summary>Server-aligned "now". Use this in place of <see cref="DateTime.Now"/>
    /// when computing how long ago something happened.</summary>
    public static DateTime ServerNow =>
        DateTime.Now - TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref _offsetTicks));

    /// <summary>Record the server's current time from an HTTP `Date` header
    /// and update the offset.</summary>
    public static void RecordServerTime(DateTimeOffset serverTime)
    {
        var newOffset = DateTime.UtcNow - serverTime.UtcDateTime;
        var prevTicks = System.Threading.Interlocked.Exchange(ref _offsetTicks, newOffset.Ticks);

        // Only log when the offset changes meaningfully — avoids spam.
        var prev = TimeSpan.FromTicks(prevTicks);
        if (Math.Abs((newOffset - prev).TotalSeconds) >= 0.5)
        {
            Plugin.Log.Information(
                $"[Faloop] Server clock offset: {newOffset.TotalSeconds:+0.0;-0.0}s " +
                "(positive = your PC is ahead of the server)");
        }
    }
}
