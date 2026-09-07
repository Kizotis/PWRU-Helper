using System.Runtime.CompilerServices;
using System.Threading;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-7, assembly-wide, in the shape this repo already uses for the log and the gate-state file
/// (<c>TestLogRedirect</c>, <c>TestGateStateRedirect</c>): a <c>[ModuleInitializer]</c> that takes
/// the two things the request path would otherwise WAIT on — <c>HttpProviderCore</c>'s back-off and
/// its rate-ceiling wait — and turns them into a recorded request plus a jump forward in the
/// registry's clock.
///
/// <para>Two properties come out of that, and both are the point. <b>Nothing sleeps</b> (CI-3
/// forbids asserting timing with a sleep, and the four end-to-end cases that used to pay ~900 ms
/// each of real production back-off now pay none). And <b>the token bucket still refuses</b>: a
/// case that issues three requests in a row really is made to wait by §5.4's ceiling — the wait is
/// simply credited to <see cref="TestVirtualTime"/> instead of to the wall clock, so the bucket
/// refills exactly as it would have, in no time at all.</para>
///
/// <para>Every back-off assertion is therefore made on the <b>requested</b> delay
/// (<see cref="Delays"/>), never on elapsed time.</para>
/// </summary>
internal static class TestBackoffRedirect
{
    private static readonly object Sync = new();
    private static readonly List<TimeSpan> Recorded = new();

    /// <summary>Every delay the core asked for since the last <see cref="Reset"/>, oldest first.</summary>
    internal static IReadOnlyList<TimeSpan> Delays { get { lock (Sync) return Recorded.ToList(); } }

    internal static void Reset() { lock (Sync) Recorded.Clear(); }

    [ModuleInitializer]
    internal static void Redirect()
    {
        HttpProviderCore.DelayOverride = (delay, ct) =>
        {
            // A real Task.Delay observes the token, and a "a cancelled call stops waiting" case has
            // to be able to fail here rather than sail through.
            ct.ThrowIfCancellationRequested();
            lock (Sync) Recorded.Add(delay);
            TestVirtualTime.Advance(delay);
            return Task.CompletedTask;
        };
        ProviderGates.Clock = TestVirtualTime.Now;
    }
}

/// <summary>
/// The registry's clock for the whole test run: the wall clock plus whatever the suite has been
/// made to "wait". Real time still passes — this is not a frozen clock, so nothing that depends on
/// a forward-moving now changes shape — and a requested delay is credited the instant it is asked
/// for, which is what lets the token bucket be exercised for real without a single sleep.
/// </summary>
internal static class TestVirtualTime
{
    private static long _advanceTicks;

    internal static DateTimeOffset Now() =>
        DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _advanceTicks));

    internal static void Advance(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) Interlocked.Add(ref _advanceTicks, delay.Ticks);
    }
}
