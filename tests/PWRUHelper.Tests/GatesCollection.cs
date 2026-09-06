using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-5 / risk R-08. <c>ProviderGates</c> is static, and xUnit parallelises <b>collections</b> by
/// default: two cases in different collections would otherwise hand each other a half-open gate or
/// a fake clock. Everything that touches <c>ProviderGates</c> joins this collection, exactly as
/// everything that touches the WPF host joins <c>WpfCollection</c> (<c>StaTestHost.cs:73-74</c>).
///
/// <para>Two non-parallel collections is the accepted, bounded cost of two static facades (CI-5).
/// <b>Do not change the runner's default parallelism to work around it</b> — the suite must stay
/// green with no <c>xunit.runner.json</c> at all, which is this story's definition of done.</para>
///
/// <para><c>DisableParallelization</c> also keeps this collection from running beside any other one;
/// it costs nothing (these cases are pure arithmetic) and it means a future test that reaches
/// <c>ProviderGates</c> without joining the collection is a bug this file can still survive.</para>
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
    protected GatesTestBase() => ProviderGates.ResetForTests();

    public void Dispose()
    {
        // After, as well as before: a case that leaves a fake clock or a temp path behind would
        // otherwise reach whatever runs next — including a case in another collection, since only
        // this collection is serialised, not the whole assembly.
        ProviderGates.ResetForTests();
        GC.SuppressFinalize(this);
    }
}
