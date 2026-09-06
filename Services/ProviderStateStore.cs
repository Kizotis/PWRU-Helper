using System.IO;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>
/// One provider's persisted gate state — §5.7's per-provider object, as a value. It carries the
/// breaker and nothing else: the rate-ceiling bucket and the half-open probe latch are deliberately
/// absent (§5.7, I9). A bucket restored from a file written 40 minutes ago is either full, so
/// restoring it was pointless, or a fabricated debt; a probe latch restored from disk would be a
/// probe nobody is running, i.e. a gate that admits no one for a full timeout at every start.
///
/// <para><b>Two block timelines, because ruling E2-i gives them two different exits.</b>
/// <see cref="BlockedUntil"/> is §5.7's own field and is the <i>IP-scoped</i> one (RateLimited,
/// Blocked and the soft rows — the provider's counter on the address this PC dials from, which only
/// time lifts). <see cref="KeyBlockedUntil"/> is the <i>account-scoped</i> one that a key save lifts
/// — in practice only <c>QuotaExhausted</c>, since <c>AuthFailed</c> is never persisted (ruling
/// E2-a). It is an addition to §5.7's shape, made because collapsing the two into one field loses
/// E2-i across a restart: a key save would either lift a 429 window it must not touch, or fail to
/// lift the quota it must. A file without the field simply has no account-scoped block, so old and
/// new files read either way inside <c>version 1</c>.</para>
///
/// <para><see cref="LastKind"/> is the <see cref="TranslationErrorKind"/> <b>member name</b>, never
/// its ordinal: <c>TranslationErrorsTests</c> pins today's enum order, and a file written by one
/// version has to still read on another. An unrecognised name reads back as
/// <c>TranslationErrorKind.Unknown</c> and never throws.</para>
/// </summary>
internal sealed record ProviderStateRecord(
    DateTimeOffset? BlockedUntil,
    DateTimeOffset? KeyBlockedUntil,
    int Strikes,
    string? LastKind,
    DateTimeOffset? LastAt,
    DateTimeOffset? CleanSince);

/// <summary>
/// <b>How</b> <c>%AppData%\PWRUHelper\provider-state.json</c> is read and written, and nothing else:
/// no clock, no gate, no policy, no decision about <i>when</i> — that is <see cref="ProviderGates"/>'s
/// (§5.7, glossary §16). Both halves are best-effort and neither ever throws: gate state is
/// disposable, and losing it costs one extra request, while a file that could crash the app or block
/// its start would be worth far more than it saves (R-01).
///
/// <para><b>Save</b> is the exact pattern of <c>SettingsService.Save</c>
/// (<c>SettingsService.cs:184-196</c>): create the directory, write <c>path + ".tmp"</c>,
/// <c>File.Replace</c> it over the target (or <c>File.Move</c> when there is none), the whole method
/// inside one <c>try { } catch { }</c> so a read-only disk costs nothing. Not a variant of it — the
/// same shape, so the next person recognises it.</para>
///
/// <para><b><c>version</c> is a forward guard, not a migration.</b> Anything but
/// <see cref="SchemaVersion"/> yields an empty result. There is no <c>Migrate</c> here and there must
/// not be one: unlike <c>settings.json</c>, this file holds nothing the user typed.</para>
///
/// <para><b>Unknown provider ids are preserved verbatim</b> (AC 5), as the raw
/// <see cref="JsonElement"/> that was read, not re-serialised through
/// <see cref="ProviderStateRecord"/> — a field this build does not know about would be dropped by the
/// round trip, so a user who downgrades after trying a build that knows <c>edge</c> would lose that
/// gate's state.</para>
/// </summary>
internal static class ProviderStateStore
{
    /// <summary>§5.7's <c>version</c>. Bumped only by a change that an older build could
    /// misread — and a bump means older builds silently start from an empty registry, which is the
    /// accepted cost.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>What a load produced: the entries this build understands, typed, and the ones it
    /// does not, verbatim. Both are empty on every failure path.
    ///
    /// <para><see cref="KeepFile"/> separates the two reasons a load can come back empty, because
    /// they call for opposite things on the way out. <b>Empty because the file said nothing this
    /// build can use</b> — absent, truncated, wrong-rooted, absurdly large — means the file is ours
    /// to replace. <b>Empty because we could not read it</b> (a sharing violation from an AV or a
    /// sync agent, an ACL hiccup) <b>or because a NEWER build wrote it</b> (<c>version</c> we do
    /// not understand) means it must be left alone: overwriting it would erase a standing pause and
    /// every preserved unknown id — AC 5's whole purpose — over a 50 ms lock or a downgrade, which
    /// is a far worse outcome than not persisting this session.</para></summary>
    internal sealed record LoadResult(
        IReadOnlyDictionary<string, ProviderStateRecord> Providers,
        IReadOnlyDictionary<string, JsonElement> Unknown,
        bool KeepFile = false);

    /// <summary>
    /// The most this file may be before it is refused <b>unread</b>. A real one is a few hundred
    /// bytes — six ids, six short objects — and even a file carrying a dozen unknown ids from a
    /// newer build does not reach a kilobyte. Anything past 64 KB is a hand edit, a corruption, or
    /// something hostile, and it is not worth a byte of what it would cost: this read happens
    /// <b>under the registry's lock</b> (<c>ProviderGates.EnsureLoaded</c>), which
    /// <c>MainWindow.OnClosing</c>'s flush and every concurrent first request wait on. Paging a
    /// multi-megabyte file into a string and through <c>JsonDocument.Parse</c> there is a window
    /// that will not close and a request that will not start; refusing it costs one extra request
    /// to a provider. Bounded before it is opened, so the size is never the thing that is read.
    /// </summary>
    private const long MaxBytes = 64 * 1024;

    /// <summary>The highest strike count worth reading back. The ladder saturates at
    /// <c>OpenCapMinutes</c> within a handful of rungs, so anything past this is a hand edit or
    /// bit-rot — and left unclamped at the top it is actively harmful: seeded at
    /// <c>int.MaxValue</c>, the next <c>_strikes++</c> overflows.</summary>
    private const int MaxStrikes = 64;

    private static readonly LoadResult Empty = new(
        new Dictionary<string, ProviderStateRecord>(StringComparer.Ordinal),
        new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    // Indented like settings.json: this file sits in the folder users are told to zip and send, so
    // it has to be readable by a human (I11 — ids, timings and kinds only; no user text, no URL,
    // no key). One options object for the life of the process, per the footprint rule.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The file, or nothing. Missing, truncated, wrong-rooted, future-versioned, a field that is not
    /// a date — every one of them is an empty result and no exception (AC 3). Fields are read one by
    /// one rather than deserialised as an object so a single bad value costs its own field and not
    /// the whole file.
    /// </summary>
    internal static LoadResult Load(string path)
    {
        try
        {
            // FileInfo rather than File.Exists so the size is known before anything is read: a
            // directory, a missing file and an absurd one all leave here without an allocation.
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return Empty;   // see MaxBytes

            // ReadAllText, so a file another process holds open for writing throws a sharing
            // violation into the catch below and yields "no state" immediately — this read may
            // never wait on a lock somebody else owns (R-01: nothing here may pause the app).
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v)) return Empty;

            // A version we do not understand is a file a NEWER build wrote. Empty registry, as the
            // forward guard says — but do NOT let the next transition rewrite it: AC 5 preserves a
            // newer build's unknown ids one by one and would be defeated wholesale here, because a
            // schema bump is exactly the downgrade that makes preservation matter. The cost, taken
            // knowingly: a `version` corrupted to something else costs persistence until the user
            // deletes a file the app already documents as disposable.
            if (v != SchemaVersion) return Empty with { KeepFile = true };

            if (!root.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Object) return Empty;

            var known = new Dictionary<string, ProviderStateRecord>(StringComparer.Ordinal);
            var unknown = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            foreach (var entry in providers.EnumerateObject())
            {
                // An id this build has no gate for is kept exactly as it was read (AC 5). Clone,
                // because the JsonDocument this element belongs to is disposed on the way out.
                if (!ProviderIds.All.Contains(entry.Name, StringComparer.Ordinal))
                {
                    unknown[entry.Name] = entry.Value.Clone();
                    continue;
                }

                if (entry.Value.ValueKind != JsonValueKind.Object) continue;

                known[entry.Name] = new ProviderStateRecord(
                    Date(entry.Value, "blockedUntil"),
                    Date(entry.Value, "keyBlockedUntil"),
                    // Clamped at BOTH ends. A negative strike count is nonsense; so is a huge one,
                    // and that end bites harder — seeded at int.MaxValue, the next `_strikes++`
                    // overflows and the ladder reports a negative count to E7 and writes it back.
                    // 64 is already far past the rung at which EscalatedWindow saturates.
                    Math.Clamp(Int(entry.Value, "strikes"), 0, MaxStrikes),
                    Text(entry.Value, "lastKind"),
                    Date(entry.Value, "lastAt"),
                    Date(entry.Value, "cleanSince"));
            }

            return new LoadResult(known, unknown);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // We could not read the BYTES — the file is held open by an AV or a sync agent, or the
            // ACL says no. The state is unavailable, not absent, and the difference matters on the
            // way out: rewriting it from an empty registry would destroy a standing pause and every
            // preserved unknown id over a transient lock. Leave it exactly where it is.
            return Empty with { KeepFile = true };
        }
        catch (Exception)
        {
            // Everything else is a file whose CONTENT this build cannot use — truncated JSON,
            // invalid UTF-16 from a hand edit — like GatePolicy.Parse and SettingsService.Load.
            // That file is ours to replace: refusing to would wedge persistence for good on a
            // corruption the next write would have healed.
            return Empty;
        }
    }

    /// <summary>
    /// The whole file, atomically. <paramref name="unknown"/> is re-emitted verbatim beside the
    /// typed entries; a provider with nothing worth persisting is simply absent, which is why a
    /// session that never saw a failure leaves a file holding an empty <c>providers</c> object
    /// rather than six pristine ones.
    /// </summary>
    internal static void Save(string path,
                              IReadOnlyDictionary<string, ProviderStateRecord> providers,
                              IReadOnlyDictionary<string, JsonElement>? unknown)
    {
        try
        {
            // object-typed values on purpose: System.Text.Json serialises each by its runtime type,
            // so a preserved JsonElement is written back byte-for-byte while a record goes through
            // the camelCase policy.
            var all = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in providers) all[pair.Key] = pair.Value;
            if (unknown != null)
                // TryAdd, not an assignment: a LIVE gate always beats a preserved blob. The
                // collision cannot happen through Load, which classifies by ProviderIds.All — but
                // For(string) takes any id and validates none, so E3.S6's google-gtx rename is one
                // call site away from a gate that is in the registry and "unknown" to the file at
                // the same time, and the wrong order would replace its real block with stale JSON.
                foreach (var pair in unknown) all.TryAdd(pair.Key, pair.Value);

            var json = JsonSerializer.Serialize(
                new StateFile(SchemaVersion, all), Options);

            // Empty for a bare filename, null for a volume root — CreateDirectory throws on both,
            // and the throw would land in the catch below and kill every save for good, silently.
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Temp file first, then swap it in, exactly as SettingsService.Save does: a crash
            // mid-write can never leave a truncated file — and a truncated one would be read as
            // "no state", i.e. an app that starts up hammering the provider it was told to leave
            // alone. "Crash" means the process dying, not the machine: WriteAllText does not
            // fsync, so a power cut can still commit the rename ahead of the bytes. That is the
            // same guarantee settings.json has had for eleven releases, and the worst it costs
            // here is the one thing this file is already allowed to cost — one extra request.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception)
        {
            // Not writable — the pause just will not survive this restart. Never a failed request.
        }
    }

    /// <summary>The root object, as a type rather than an anonymous one so the naming policy and
    /// the shape are both obvious at a glance.</summary>
    private sealed record StateFile(int Version, IReadOnlyDictionary<string, object> Providers);

    // ---- one field at a time, none of which throws ------------------------------------------

    private static DateTimeOffset? Date(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && p.TryGetDateTimeOffset(out var v) ? v : null;

    private static int Int(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var v) ? v : 0;

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    /// <summary>The <c>lastKind</c> name as a kind. An unrecognised one is <c>Unknown</c> rather
    /// than an exception or a silent null: a file written by a build with one more row in §5.3 must
    /// still read here (T1). <c>null</c> stays null — "no failure yet" is not "Unknown".</summary>
    internal static TranslationErrorKind? ParseKind(string? name) =>
        name is null ? null
        // Enum.TryParse alone is not "the member name": it also accepts the ORDINAL form ("7"),
        // and a comma-separated list ("AuthFailed, Unknown"), which it OR-combines even for an
        // enum with no [Flags]. Both are exactly what storing the name exists to avoid — the
        // ordinal makes the file depend on the enum's ORDER, which TranslationErrorsTests pins
        // only for today's build, and the list form produces a value that is not equal to
        // AuthFailed and so walks straight past ruling E2-a's drop, into _lastKind and back out to
        // disk. A name starts with a letter, carries no comma, and has to be one this build
        // defines; anything else is a kind from a newer §5.3 and reads as Unknown, never throws.
        : name.Length > 0 && char.IsLetter(name[0]) && !name.Contains(',')
          && Enum.TryParse<TranslationErrorKind>(name, ignoreCase: true, out var kind)
          && Enum.IsDefined(kind) ? kind
        : TranslationErrorKind.Unknown;
}
