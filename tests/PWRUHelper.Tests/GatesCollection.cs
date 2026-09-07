using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-5 / risk R-08. <c>ProviderGates</c> is static, and xUnit parallelises <b>collections</b> by
/// default: two cases in different collections would otherwise hand each other a half-open gate or
/// a fake clock. Everything that touches <c>ProviderGates</c> joins this collection, exactly as
/// everything that touches the WPF host joins <c>WpfCollection</c> (<c>StaTestHost.cs:73-74</c>).
///
/// <para>A second non-parallel collection is the accepted, bounded cost of a second static facade
/// (CI-5). <b>Do not change the runner's default parallelism to work around it</b> — the suite must
/// stay green with no <c>xunit.runner.json</c> at all, which is this story's definition of done.</para>
///
/// <para><c>DisableParallelization</c> also keeps this collection from running beside any other one
/// (<c>WpfCollection</c> does not set it, so "two non-parallel collections" would overstate what is
/// there); it costs nothing — these cases are pure arithmetic — and it means a future test that
/// reaches <c>ProviderGates</c> without joining the collection is a bug this file can still survive.
/// <c>ProviderGatesTests.Every_test_class_that_touches_the_registry_joins_this_collection</c> is what
/// stops that bug from being written in the first place.</para>
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
