using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-5 / risk R-08. <c>ProviderGates</c> is static, and xUnit parallelises <b>collections</b> by
/// default: two cases in different collections would otherwise hand each other a half-open gate or
/// a fake clock. Everything that touches <c>ProviderGates</c> joins this collection — <b>and since
/// E7.S8 that includes every STA class</b>, because a real <c>MainWindow</c> reaches the registry
/// through <c>TranslationChains</c> whether or not the test file names it.
///
/// <para><b>Measured, and the reason the "WPF" collection no longer exists.</b>
/// <c>DisableParallelization</c> did <b>not</b> keep this collection from running beside the STA
/// one. Reproduced deterministically (E7.S8): a 2.5 s sleep inserted into
/// <c>TemplateRenderTests.A_new_window_has_not_read_the_gate_state_file_when_it_paints_the_chip</c>
/// — between writing the redirected <c>provider-state.json</c> and building the window — made that
/// case fail on a <b>30-minute rate-limit pause opened by a <c>PerLineFallbackTests</c> case in
/// THIS collection</b>, with only those two classes selected. The two collections interleave; the
/// attribute is kept because it also serialises this collection against itself, but it is not what
/// makes the suite safe. <b>One collection is.</b> That race is the flake E7.S7's review saw in 2
/// of 7 full runs, in both directions: the STA case reading a pause a gate case had just opened,
/// and the gate cases reading the <c>blockedUntil: 2099</c> file the STA case writes.</para>
///
/// <para>A single non-parallel collection is the accepted, bounded cost of a static facade plus a
/// single-threaded STA host (CI-5). <b>Do not change the runner's default parallelism to work
/// around it</b> — the suite must stay green with no <c>xunit.runner.json</c> at all, which is that
/// story's definition of done, and the ~40 pure-logic classes still run in parallel.</para>
///
/// <para><c>ProviderGatesTests.Every_test_class_that_touches_the_registry_joins_this_collection</c>
/// is what stops the hole from being re-opened: it triggers on the registry, on
/// <c>GatesTestBase</c>, on the redirected gate-state <b>file</b> (which is how
/// <c>TemplateRenderTests</c> slipped past it) and on the STA host.</para>
/// </summary>
[CollectionDefinition("Gates", DisableParallelization = true)]
public class GatesCollection { }

/// <summary>
/// The resetting fixture IS-5 asks for. It has to be a base class rather than an
/// <c>ICollectionFixture</c>: a collection fixture is constructed <i>once for the collection</i>,
/// and static state has to be cleared <b>before and after every case</b>, which in xUnit is the
/// test class's constructor and <c>Dispose</c>.
/// </summary>
public abstract class GatesTestBase : IDisposable
{
    protected GatesTestBase() => Reset();

    /// <summary>The same reset, mid-case, for a case that drives TWO provider failures and needs
    /// the second one to meet a gate that has not just been closed by the first. Exposed as a
    /// method rather than as a call to <c>ProviderGates.ResetForTests</c> at the call site because
    /// the scan in <c>ProviderGatesTests</c> asks every file that names the registry to join the
    /// non-parallel collection, and a case in the log-file collection cannot.</summary>
    protected static void ResetGates() => Reset();

    /// <summary>Derived teardown. Override this rather than <c>Dispose</c>: re-declaring
    /// <c>IDisposable</c> on a derived class would re-map the interface and xUnit would then call
    /// the derived method <i>instead</i> of this one, silently skipping the reset — a leaked fake
    /// clock that makes the next case pass or fail depending on what ran before it.</summary>
    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        // After, as well as before: a case that leaves a fake clock or a temp path behind would
        // otherwise reach whatever runs next — including a case in another collection, since only
        // this collection is serialised, not the whole assembly.
        DisposeCore();
        Reset();
        GC.SuppressFinalize(this);
    }

    /// <summary>`ResetForTests` puts the process back where it started — which includes a null
    /// <c>PathOverride</c>, i.e. the developer's real <c>%AppData%</c>. That is right for the
    /// production contract and wrong for a test run, so the assembly-wide redirect is re-applied
    /// immediately: between the two there is no instant in which a gate case can reach the real
    /// file. A case that wants its own file still opens a <see cref="TempGateState"/>.</summary>
    private static void Reset()
    {
        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = TestGateStateRedirect.Path;
        // Same reasoning as the path, for the clock IS-7 needs: `ResetForTests` restores the WALL
        // clock (the production contract), and a wall clock cannot be advanced — so a case that
        // drives a provider through the §5.4 ceiling would have to sleep for the bucket to refill.
        // Re-applying the run-wide virtual clock here keeps the bucket real and the suite silent.
        // A case that wants its own fake clock still sets one, exactly as before.
        ProviderGates.Clock = TestVirtualTime.Now;
        TestBackoffRedirect.Reset();
    }
}
