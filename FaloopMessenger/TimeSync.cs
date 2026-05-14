using System;

namespace FaloopMessenger;

// Tracks the offset between the local PC clock and Faloop's server clock so
// "X seconds ago" displays match what the Faloop website shows. We feed this
// from the HTTP `Date` header on responses from faloop.app (no extra calls
// needed — we already hit /api/auth/user/refresh on every connect).
internal static class TimeSync
{
    private static TimeSpan _offset = TimeSpan.Zero;   // local − server

    /// <summary>Server-aligned "now". Use this in place of <see cref="DateTime.Now"/>
    /// when computing how long ago something happened.</summary>
    public static DateTime ServerNow => DateTime.Now - _offset;

    /// <summary>Record the server's current time from an HTTP `Date` header
    /// and update the offset.</summary>
    public static void RecordServerTime(DateTimeOffset serverTime)
    {
        var newOffset = DateTime.UtcNow - serverTime.UtcDateTime;
        // Only log when the offset changes meaningfully — avoids spam.
        if (Math.Abs((newOffset - _offset).TotalSeconds) >= 0.5)
        {
            Plugin.Log.Information(
                $"[Faloop] Server clock offset: {newOffset.TotalSeconds:+0.0;-0.0}s " +
                "(positive = your PC is ahead of the server)");
        }
        _offset = newOffset;
    }
}
