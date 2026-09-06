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
    /// does not, verbatim. Both are empty on every failure path.</summary>
    internal sealed record LoadResult(
        IReadOnlyDictionary<string, ProviderStateRecord> Providers,
        IReadOnlyDictionary<string, JsonElement> Unknown);

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
            if (!File.Exists(path)) return Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != SchemaVersion) return Empty;

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
                    Math.Max(0, Int(entry.Value, "strikes")),   // a negative strike count is nonsense
                    Text(entry.Value, "lastKind"),
                    Date(entry.Value, "lastAt"),
                    Date(entry.Value, "cleanSince"));
            }

            return new LoadResult(known, unknown);
        }
        catch (Exception)
        {
            // Deliberately every exception, like GatePolicy.Parse and SettingsService.Load: an
            // unreadable file, a locked one, invalid UTF-16 from a hand edit. There is no failure
            // of this read that should be louder than "no state".
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
                foreach (var pair in unknown) all[pair.Key] = pair.Value;

            var json = JsonSerializer.Serialize(
                new StateFile(SchemaVersion, all), Options);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Temp file first, then swap it in, exactly as SettingsService.Save does: a crash
            // mid-write can never leave a truncated file — and a truncated one would be read as
            // "no state", i.e. an app that starts up hammering the provider it was told to leave
            // alone.
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
        : Enum.TryParse<TranslationErrorKind>(name, ignoreCase: true, out var kind) ? kind
        : TranslationErrorKind.Unknown;
}
