using System;
using System.Globalization;

namespace TikMan.Core.Storage;

/// <summary>When the scheduled update check is due.
///
/// <para>Its own type in Core, and pure, for two reasons: the rule is the one genuinely awkward part of a
/// nightly schedule (you cannot test it by waiting until 03:00), and it is needed by more than one client –
/// keeping a private copy per client is how the two quietly drift apart.</para></summary>
public static class AutoCheckSchedule
{
    /// <summary>Is <paramref name="now"/> past today's slot, with no run recorded for it yet?
    ///
    /// <para>⚠️ "Has passed and wasn't run", not "is now". TikMan is a desktop app, so 03:00 is a time
    /// nobody may have it open for. A slot missed because the app was closed is caught up the next time it
    /// is open – that is the difference between a schedule that mostly works and one that only works on
    /// machines that never sleep.</para>
    ///
    /// <para><paramref name="last"/> null means "never ran", which is due. An unparseable
    /// <paramref name="slot"/> is never due – better a schedule that does nothing than one that fires at a
    /// time the user did not mean.</para></summary>
    public static bool IsDueNow(DateTime now, string slot, DateTime? last)
    {
        if (!TimeSpan.TryParseExact(slot, new[] { @"h\:mm", @"hh\:mm" },
                CultureInfo.InvariantCulture, out var time)) return false;
        var todaysSlot = now.Date + time;
        if (now < todaysSlot) return false;          // not yet
        return last is null || last < todaysSlot;    // not run for this slot
    }
}
