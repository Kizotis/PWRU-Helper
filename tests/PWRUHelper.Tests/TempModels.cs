using System.IO;
using System.Security.Cryptography;
using System.Text;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-3's fifth instance, and the one with the most to lose: points
/// <see cref="OfflineModelStore.PathOverride"/> at a throwaway model root for the duration of a
/// test, and <see cref="OfflineModelManifest.Override"/> at a table of the test's own so the whole
/// download-and-verify path is provable with <b>nothing leaving the box</b> (CI-8 forbids the model
/// download by name, and this class is the one that would write 50 MB).
///
/// <para>Mirrors <c>TempCache.cs</c> deliberately, down to the swallowed delete and the restore to
/// the run-wide redirect rather than to <c>null</c>: null is the developer's real
/// <c>%LocalAppData%</c>, and the point of the pair is that no instant of a test run resolves
/// there.</para>
/// </summary>
internal sealed class TempModels : IDisposable
{
    private readonly DirectoryInfo _dir;

    /// <summary>The throwaway root <see cref="OfflineModelStore.Root"/> resolves to while this
    /// object lives. It is created by <see cref="Directory.CreateTempSubdirectory"/> and then
    /// deleted again, because half the point of the suite is that the store creates its own root
    /// and a failure removes it.</summary>
    public string Root { get; }

    public TempModels(OfflineModelManifest? manifest = null)
    {
        _dir = Directory.CreateTempSubdirectory("pwru-models-");
        Root = Path.Combine(_dir.FullName, "models");
        OfflineModelStore.PathOverride = Root;
        OfflineModelManifest.Override = manifest;
    }

    public void Dispose()
    {
        OfflineModelStore.PathOverride = TestModelsRedirect.Path;
        OfflineModelManifest.Override = null;
        try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
    }

    // ---- fixtures ---------------------------------------------------------------------------

    /// <summary>Bytes that are cheap to make, cheap to hash and recognisable in a failure message.
    /// Nothing here is 22 MB: the point of the seam is that the SHAPE is provable without the
    /// size.</summary>
    public static byte[] Blob(byte seed, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A two-file manifest — one native library, one model file for <c>ru-en</c> — with
    /// REAL digests over the fixture bytes, so verification succeeds when it should and can be made
    /// to fail by changing one byte of what the handler serves.</summary>
    public static OfflineModelManifest ManifestOver(byte[] native, byte[] model, string tag = "test-tag")
        => new(tag, new[]
        {
            new OfflineFile(OfflineModelManifest.NativeFileName, native.Length, Sha256(native), null),
            new OfflineFile("model.ruen.intgemm.alphas.bin", model.Length, Sha256(model),
                            OfflineModelManifest.RuEn),
        });

    /// <summary>Everything under the root, relative and sorted — the assertion shape for "nothing
    /// was installed" and for "Remove deleted every file".</summary>
    public IReadOnlyList<string> Files()
    {
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Put a complete, verifiable install on disk without a download — the fixture the
    /// "already installed" cases need. It writes exactly what <c>InstallAsync</c> would.</summary>
    public void Install(OfflineModelManifest manifest, byte[] native, byte[] model)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, OfflineModelManifest.NativeFileName), native);

        var pair = Path.Combine(Root, OfflineModelManifest.RuEn);
        Directory.CreateDirectory(pair);
        File.WriteAllBytes(Path.Combine(pair, "model.ruen.intgemm.alphas.bin"), model);
        File.WriteAllText(Path.Combine(pair, OfflineModelManifest.ConfigFileName),
                          OfflineModelManifest.ConfigText(OfflineModelManifest.RuEn),
                          new UTF8Encoding(false));

        Assert.True(new OfflineModelStore(manifest: manifest).IsInstalled,
                    "the fixture did not produce an install the store recognises");
    }
}
