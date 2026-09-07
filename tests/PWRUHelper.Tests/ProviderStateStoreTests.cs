using System.IO;
using System.Linq;
using System.Text.Json;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E2.S4 — <c>provider-state.json</c>: the pause survives the restart, and nothing else does.
///
/// <para>Every case joins the non-parallel <c>Gates</c> collection (IS-5) and runs against a
/// throwaway file (IS-3): the registry is process-global static state and the file it writes sits
/// next to the developer's own <c>settings.json</c>. No case sleeps and no case waits on the 1 s
/// debounce (CI-3) — <see cref="ProviderGates.Flush"/> is the seam, and
/// <c>ProviderGates.SaveDebounceMs</c> is pushed out of the way where a case has to prove that
/// nothing was written <i>yet</i>.</para>
/// </summary>
[Collection("Gates")]
public class ProviderStateStoreTests : GatesTestBase
{
    private sealed class FakeClock
    {
        // 2001, like ProviderGatesTests' own: visibly not the wall clock, so a leaked clock or a
        // load that silently fell back to DateTimeOffset.UtcNow cannot pass as a restored window.
        public DateTimeOffset Now { get; private set; } = new(2001, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(TranslationPolicy.OpenCapMinutes);
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds);

    /// <summary>A minimal well-formed file with one blocked provider, written by hand: the corrupt
    /// and skewed inputs are short inline strings rather than fixtures (IS-9's budget).</summary>
    private static string OneProvider(string id, string blockedUntil, int strikes = 1,
                                      string lastKind = "RateLimited", string? keyBlockedUntil = null)
        => $$"""
             { "version": 1, "providers": { "{{id}}": {
                 "blockedUntil": {{blockedUntil}},
                 "keyBlockedUntil": {{keyBlockedUntil ?? "null"}},
                 "strikes": {{strikes}},
                 "lastKind": "{{lastKind}}",
                 "lastAt": null,
                 "cleanSince": null } } }
             """;

    private static string Iso(DateTimeOffset t) => "\"" + t.ToString("o") + "\"";

    // ---- TP-GATE-19: the round trip ------------------------------------------------------------

    [Fact]
    public void A_pause_survives_a_restart()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var before = gate.Snapshot();
        ProviderGates.Flush();

        Assert.True(File.Exists(temp.Path), "a transition must reach the file by the time Flush returns");

        // The "restart": the registry goes away, the file does not.
        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = temp.Path;
        ProviderGates.Clock = clock.Read;

        Assert.Null(ProviderGates.Snapshot(ProviderIds.GoogleDict));   // nothing loaded yet (I10)

        var decision = ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive);

        Assert.Equal(GateOutcome.Open, decision.Outcome);
        var after = ProviderGates.Snapshot(ProviderIds.GoogleDict)!;
        Assert.Equal(GateState.Open, after.State);
        Assert.Equal(before.BlockedUntil, after.BlockedUntil);     // to the tick, not to the second
        Assert.Equal(clock.Now + Base, after.BlockedUntil);
        Assert.Equal(before.Strikes, after.Strikes);
        Assert.Equal(TranslationErrorKind.RateLimited, after.LastKind);
    }

    [Fact]
    public void The_file_on_disk_is_the_5_7_shape()
    {
        // The schema is a contract with the NEXT build, not an implementation detail: a rename here
        // silently discards every user's state on their next upgrade, and nothing else would notice.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();

        using var doc = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());

        var entry = doc.RootElement.GetProperty("providers").GetProperty(ProviderIds.GoogleDict);
        Assert.Equal(new[] { "blockedUntil", "keyBlockedUntil", "strikes", "lastKind", "lastAt", "cleanSince" },
                     entry.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(clock.Now + Base, entry.GetProperty("blockedUntil").GetDateTimeOffset());
        Assert.Equal(1, entry.GetProperty("strikes").GetInt32());
        // The member NAME, never the ordinal: TranslationErrorsTests pins today's enum order, and a
        // file written by one version has to still read on another.
        Assert.Equal("RateLimited", entry.GetProperty("lastKind").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("cleanSince").ValueKind);
    }

    [Fact]
    public void The_restored_strike_ladder_keeps_escalating_where_it_left_off()
    {
        // The strikes are the half of the state that makes a restart NOT a way to get a fresh
        // 60-second window out of a provider that has already said stop four times.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.GoogleGtx);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportFailure(TranslationErrorKind.RateLimited);      // strike 2 → 120 s
        ProviderGates.Flush();

        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = temp.Path;
        ProviderGates.Clock = clock.Read;

        var restored = ProviderGates.For(ProviderIds.GoogleGtx);
        restored.TryEnter(RequestPriority.Interactive);             // loads
        Assert.Equal(2, restored.Snapshot().Strikes);

        clock.Advance(TimeSpan.FromMinutes(3));                     // past the restored window
        var probe = restored.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, probe.Outcome);
        restored.ReportFailure(TranslationErrorKind.RateLimited, probeToken: probe.ProbeToken);

        // Strike 3, not strike 1: the ladder continued across the restart.
        Assert.Equal(3, restored.Snapshot().Strikes);
        Assert.Equal(clock.Now + Base * 4, restored.Snapshot().BlockedUntil);
    }

    // ---- TP-GATE-20: missing, corrupt, wrong version -------------------------------------------

    [Fact]
    public void A_missing_file_leaves_the_registry_empty_and_the_app_unblocked()
    {
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;

        Assert.False(File.Exists(temp.Path));
        Assert.Equal(GateOutcome.Allow,
            ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Theory]
    // truncated mid-object — the shape a crash during a non-atomic write would leave
    [InlineData("{ \"version\": 1, \"providers\": { \"google-dict\": { \"blockedUntil\": ")]
    [InlineData("")]                                     // zero bytes
    [InlineData("[]")]                                   // a root that is an array
    [InlineData("\"nope\"")]                             // a root that is a string
    [InlineData("{ \"providers\": { } }")]               // no version at all
    [InlineData("{ \"version\": 99, \"providers\": { \"google-dict\": { \"blockedUntil\": \"9999-01-01T00:00:00+00:00\" } } }")]
    [InlineData("{ \"version\": \"1\", \"providers\": { } }")]   // version as a string
    [InlineData("{ \"version\": 1, \"providers\": 42 }")]        // providers is not an object
    [InlineData("{ \"version\": 1, \"providers\": { \"google-dict\": 7 } }")]   // the entry is not an object
    public void A_corrupt_or_future_version_file_yields_an_empty_registry(string json)
    {
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;
        File.WriteAllText(temp.Path, json);

        // No throw, nothing restored, nothing blocked: a disposable file may never be what stops
        // the app translating (AC 3 / R-01).
        Assert.Equal(GateOutcome.Allow,
            ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateState.Closed, ProviderGates.Snapshot(ProviderIds.GoogleDict)!.State);
    }

    [Fact]
    public void A_field_that_is_not_a_date_or_a_number_is_ignored_rather_than_fatal()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path, """
            { "version": 1, "providers": { "google-dict": {
                "blockedUntil": "the day after tomorrow",
                "strikes": -4,
                "lastKind": "MadeUpKind",
                "lastAt": 17,
                "cleanSince": null } } }
            """);

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);

        var snap = gate.Snapshot();
        Assert.Null(snap.BlockedUntil);                        // not a date ⇒ no block
        Assert.Equal(0, snap.Strikes);                         // negative strikes ⇒ none
        Assert.Equal(TranslationErrorKind.Unknown, snap.LastKind);   // an unknown name ⇒ Unknown

        // The TOP end bites harder than the bottom, and is read straight from the store so the
        // registry's one-load-per-process rule does not get in the way: seeded at int.MaxValue the
        // next `_strikes++` overflows, and the ladder then reports a negative count to E7's status
        // list and writes it back to the file.
        File.WriteAllText(temp.Path, OneProvider(ProviderIds.Edge, "null", strikes: int.MaxValue));
        Assert.InRange(ProviderStateStore.Load(temp.Path).Providers[ProviderIds.Edge].Strikes, 0, 64);
    }

    [Fact]
    public void An_absurdly_large_file_is_refused_unread()
    {
        // The read runs UNDER the registry's lock, which OnClosing's flush and every concurrent
        // first request wait on. A hand-edited or corrupted file must therefore be bounded before
        // it is opened: refusing costs one extra request to a provider, paging a huge one in costs
        // the window close. 64 KB is ~60× the largest file this build can write.
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;

        // Valid JSON, so nothing but the size can be what refuses it.
        var padding = new string('x', 70 * 1024);
        File.WriteAllText(temp.Path, $$"""
            { "version": 1, "providers": { "google-dict": {
                "blockedUntil": "2099-01-01T00:00:00+00:00", "strikes": 3,
                "padding": "{{padding}}" } } }
            """);
        Assert.True(new FileInfo(temp.Path).Length > 64 * 1024);

        Assert.Empty(ProviderStateStore.Load(temp.Path).Providers);
        Assert.Equal(GateOutcome.Allow,
            ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_file_another_process_holds_open_loads_as_no_state_without_waiting()
    {
        // A sharing violation is one of the "every one of them" AC 3 covers, and the one that could
        // block rather than throw. It must come back immediately as "no state": this read is on the
        // first translation request of the session, under the lock the window close waits on.
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;
        File.WriteAllText(temp.Path, OneProvider(ProviderIds.GoogleDict, "\"2099-01-01T00:00:00+00:00\""));

        using (new FileStream(temp.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(ProviderStateStore.Load(temp.Path).Providers);
            Assert.Equal(GateOutcome.Allow,
                ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive).Outcome);
        }
    }

    [Fact]
    public void A_save_that_cannot_swap_the_file_in_leaves_the_previous_one_intact()
    {
        // Atomicity, from the only side a test can reach without a seam in the store: the swap is
        // the last thing that happens, so a failure at it leaves the PREVIOUS file — never a
        // truncated one. A truncated provider-state.json reads back as "no state", i.e. an app that
        // starts up hammering the provider it was told to leave alone.
        using var temp = new TempGateState();
        ProviderStateStore.Save(temp.Path, new Dictionary<string, ProviderStateRecord>
        {
            [ProviderIds.Azure] = new(new DateTimeOffset(2001, 1, 1, 12, 1, 0, TimeSpan.Zero),
                                      null, 1, "RateLimited", null, null),
        }, null);
        var before = File.ReadAllText(temp.Path);

        var wreck = new Dictionary<string, ProviderStateRecord>
        {
            [ProviderIds.Azure] = new(null, null, 99, "Blocked", null, null),
        };

        // Phase 1 — the TEMP file cannot be written. This is the assertion that a direct
        // File.WriteAllText(path, …) could not pass: holding `path + ".tmp"` open exclusively only
        // stops a save that goes through it, which is what makes the atomic pattern observable from
        // outside without a seam in the store. The whole method is inside one try/catch, like
        // SettingsService.Save, so it costs nothing.
        using (new FileStream(temp.Path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            ProviderStateStore.Save(temp.Path, wreck, null);

        Assert.Equal(before, File.ReadAllText(temp.Path));

        // Phase 2 — the temp file is written but the swap cannot happen. The previous file is still
        // the previous file: a crash or a failure at any point leaves the last good state, never a
        // truncated one, which would read back as "no state".
        File.Delete(temp.Path + ".tmp");
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ProviderStateStore.Save(temp.Path, wreck, null);

        Assert.Equal(before, File.ReadAllText(temp.Path));
        Assert.Equal(1, ProviderStateStore.Load(temp.Path).Providers[ProviderIds.Azure].Strikes);
    }

    [Fact]
    public void Time_spent_closed_is_not_a_clean_run()
    {
        // A file carrying strikes AND a clean run would let being CLOSED work the ladder off:
        // CleanResetMinutes is 10, so quitting for ten minutes would buy a fresh 60-second window
        // out of a provider that has already said stop three times. This build cannot write that
        // pair (a non-zero strike count always leaves an IP timeline standing, and every path that
        // sets cleanSince first tests for none) — a hand-edited or foreign-written file can.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path, $$"""
            { "version": 1, "providers": { "google-dict": {
                "blockedUntil": {{Iso(clock.Now - TimeSpan.FromHours(1))}},
                "strikes": 3, "lastKind": "RateLimited", "lastAt": null,
                "cleanSince": {{Iso(clock.Now - TimeSpan.FromHours(1))}} } } }
            """);

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        var probe = gate.TryEnter(RequestPriority.Interactive);          // loads; the window expired
        Assert.Equal(3, gate.Snapshot().Strikes);

        // An hour of "clean" that was really an hour of being shut down buys nothing: the failed
        // probe is strike 4, not strike 1.
        gate.ReportFailure(TranslationErrorKind.RateLimited, probeToken: probe.ProbeToken);
        Assert.Equal(4, gate.Snapshot().Strikes);
    }

    [Fact]
    public void A_lastKind_that_is_an_ordinal_or_a_list_is_not_trusted()
    {
        // Enum.TryParse accepts far more than a member name: the ORDINAL form, which makes the file
        // depend on an enum ORDER only today's build pins, and a comma list, which it OR-combines
        // even with no [Flags] — producing a value that is not equal to AuthFailed and so walks
        // straight past ruling E2-a's drop.
        Assert.Equal(TranslationErrorKind.RateLimited, ProviderStateStore.ParseKind("RateLimited"));
        Assert.Equal(TranslationErrorKind.RateLimited, ProviderStateStore.ParseKind("ratelimited"));
        Assert.Null(ProviderStateStore.ParseKind(null));

        foreach (var hostile in new[] { "0", "7", "999", "-1", "AuthFailed, Unknown", "Unknown, AuthFailed" })
            Assert.Equal(TranslationErrorKind.Unknown, ProviderStateStore.ParseKind(hostile));

        // …and whatever comes back is a kind this build actually defines, so nothing undefined can
        // reach _lastKind, a switch over §5.3, or E7's status line.
        foreach (var name in new[] { "MadeUpKind", "", "7", "AuthFailed, Unknown" })
            Assert.True(Enum.IsDefined(ProviderStateStore.ParseKind(name)!.Value));
    }

    // ---- TP-GATE-21: the clock-skew clamp ------------------------------------------------------

    [Fact]
    public void A_blocked_until_a_week_ahead_is_clamped_to_the_cap_on_load()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path,
            OneProvider(ProviderIds.GoogleDict, Iso(clock.Now + TimeSpan.FromDays(7)), strikes: 3));

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        var decision = gate.TryEnter(RequestPriority.Interactive);

        Assert.Equal(GateOutcome.Open, decision.Outcome);
        // The relationship, not the literal 30 (U9): a machine whose clock jumped cannot pause the
        // app for a week.
        Assert.Equal(clock.Now + Cap, gate.Snapshot().BlockedUntil);
        Assert.Equal(clock.Now + Cap, decision.RetryAt);
    }

    [Fact]
    public void A_blocked_until_in_the_past_is_simply_expired_and_the_first_caller_probes_it()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path,
            OneProvider(ProviderIds.GoogleDict, Iso(clock.Now - TimeSpan.FromHours(2))));

        // Not dropped "helpfully": the probe is the correct recovery, so the first caller gets one
        // rather than an unmetered Allow.
        var decision = ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, decision.Outcome);
        Assert.NotEqual(0, decision.ProbeToken);
    }

    [Fact]
    public void A_hand_written_MaxValue_cannot_pause_a_provider_for_ever()
    {
        // The E2-a collision, from the other side: AuthFailed's sentinel is never WRITTEN by this
        // app, but a hand-edited or downgraded file can still carry one. Every time-based window
        // read from disk is clamped, so no file can lock the user out (ruling E2-a: "no state may
        // lock the user out without a way back").
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path, OneProvider(
            ProviderIds.DeepL, Iso(DateTimeOffset.MaxValue),
            lastKind: "AuthFailed", keyBlockedUntil: Iso(DateTimeOffset.MaxValue)));

        var gate = ProviderGates.For(ProviderIds.DeepL);
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        var snap = gate.Snapshot();
        Assert.NotEqual(DateTimeOffset.MaxValue, snap.BlockedUntil);
        Assert.True(snap.BlockedUntil <= clock.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes),
            "no persisted window may outlive the longest window the policy itself can set");
        // …and the two bounds differ on purpose. The IP-scoped timeline is clamped to the 30-minute
        // cap, the account-scoped one to its OWN longest window (§15 R9, 60 min) — so what the gate
        // enforces here is the account one. Clamping both to the cap would silently halve every
        // legitimate QuotaExhausted block across a restart, and ruling E2-a's literal "clamped to
        // the cap" is exactly the line a future reader would "correct" it to.
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes),
                     snap.BlockedUntil);
        Assert.NotEqual(TranslationErrorKind.AuthFailed, snap.LastKind);   // E2-a: never read back
    }

    [Fact]
    public void MaxValue_round_trips_through_the_store_without_throwing()
    {
        // The store itself does not clamp — it has no clock and no policy (T1); it only has to
        // survive the value. ISO-8601 round-trips it, and this is the assertion that says so.
        using var temp = new TempGateState();
        ProviderStateStore.Save(temp.Path, new Dictionary<string, ProviderStateRecord>
        {
            [ProviderIds.Azure] = new(DateTimeOffset.MaxValue, null, 1, "RateLimited", null, null),
        }, null);

        var read = ProviderStateStore.Load(temp.Path);
        Assert.Equal(DateTimeOffset.MaxValue, read.Providers[ProviderIds.Azure].BlockedUntil);
    }

    // ---- E2-a: AuthFailed is never persisted ---------------------------------------------------

    [Fact]
    public void An_auth_block_is_never_written_to_the_file()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.DeepL);
        gate.ReportFailure(TranslationErrorKind.AuthFailed);
        Assert.Equal(GateState.Open, gate.Snapshot().State);        // in memory it stands…
        ProviderGates.Flush();

        // …and on disk there is nothing to restore it with (E2-a: in-memory only, a restart resets
        // it; the key is re-read from settings at startup anyway). A 401 alone leaves no file, so
        // force one to exist with a second, persistable failure on ANOTHER provider — otherwise the
        // two DoesNotContain assertions below would be running against the empty string and could
        // not fail.
        ProviderGates.For(ProviderIds.Azure).ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();

        var json = File.ReadAllText(temp.Path);
        Assert.Contains("RateLimited", json);                  // the file is real…
        Assert.DoesNotContain("AuthFailed", json);             // …and DeepL's 401 is not in it
        Assert.DoesNotContain("9999", json);
        Assert.DoesNotContain(ProviderIds.DeepL, json);

        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = temp.Path;
        ProviderGates.Clock = clock.Read;

        Assert.Equal(GateOutcome.Allow,
            ProviderGates.For(ProviderIds.DeepL).TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void An_auth_failure_and_the_key_save_that_lifts_it_create_no_file_at_all()
    {
        // Both are real §5.2 edges that E2.S6 must log, and neither changes one persisted byte —
        // E2-a keeps AuthFailed off the disk in both directions. A transition that queues a write
        // of nothing must not be what CREATES provider-state.json beside the user's settings.json
        // for a session with nothing whatever to remember.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        ProviderGates.For(ProviderIds.DeepL).ReportFailure(TranslationErrorKind.AuthFailed);
        ProviderGates.Flush();
        Assert.False(File.Exists(temp.Path), "a 401 alone has nothing to persist");

        ProviderGates.ClearAuthBlock(ProviderIds.DeepL);              // the user re-saves the key
        ProviderGates.Flush();
        Assert.False(File.Exists(temp.Path), "…and neither has the key save that lifted it");

        // The other half of the rule: an existing file IS still rewritten when the gates fall
        // silent, because it may hold state that has since expired.
        ProviderGates.For(ProviderIds.DeepL).ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();
        Assert.True(File.Exists(temp.Path));

        clock.Advance(TimeSpan.FromMinutes(2));
        var probe = ProviderGates.For(ProviderIds.DeepL).TryEnter(RequestPriority.Interactive);
        ProviderGates.For(ProviderIds.DeepL).ReportSuccess(probe.ProbeToken);   // closed, clean run
        ProviderGates.Flush();
        Assert.True(File.Exists(temp.Path));
        // Not Contains("cleanSince"): the field is emitted unconditionally, so that would pass on
        // `"cleanSince": null` and prove nothing about the clean run it is meant to check.
        using var doc = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("providers")
            .GetProperty(ProviderIds.DeepL).GetProperty("cleanSince").ValueKind);
    }

    [Fact]
    public void A_quota_block_survives_a_restart_and_a_key_save_still_lifts_only_it()
    {
        // Both timelines are persisted, because ruling E2-i gives them two different exits: a key
        // save lifts the account-scoped one and never the IP-scoped 429 window underneath it. One
        // persisted field could not tell those apart after a restart.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.DeepL);
        gate.ReportFailure(TranslationErrorKind.RateLimited);       // IP-scoped, 60 s
        gate.ReportFailure(TranslationErrorKind.QuotaExhausted);    // account-scoped, 60 min
        ProviderGates.Flush();

        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = temp.Path;
        ProviderGates.Clock = clock.Read;

        var restored = ProviderGates.For(ProviderIds.DeepL);
        restored.TryEnter(RequestPriority.Interactive);             // loads both timelines
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes),
                     restored.Snapshot().BlockedUntil);

        ProviderGates.ClearAuthBlock(ProviderIds.DeepL);            // the user re-saves the key

        // The quota is gone; the 429 window it covered is still standing (E2-i), which is only
        // possible because both timelines came back from the file.
        Assert.Equal(clock.Now + Base, restored.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_clamped_window_is_written_back_so_it_cannot_be_re_imposed_at_every_start()
    {
        // R-01 through the file. The clamp fixes the pause in MEMORY; if the corrected value never
        // reaches the disk, the next launch reads the same 2099 and clamps it again — and a gate
        // that is merely refusing callers takes no transition, so nothing else would ever rewrite
        // it. A clock set forward once would pause the provider for 30 minutes at EVERY start, for
        // ever, with no way back (ruling E2-a). Three launches, because two cannot tell the
        // difference.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path,
            OneProvider(ProviderIds.GoogleDict, Iso(clock.Now + TimeSpan.FromDays(7)), strikes: 3));

        // Launch 1: load, clamp, and nothing else at all — no transition of any kind.
        ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive);
        ProviderGates.Flush();

        var onDisk = ProviderStateStore.Load(temp.Path).Providers[ProviderIds.GoogleDict];
        Assert.Equal(clock.Now + Cap, onDisk.BlockedUntil);     // the file itself is corrected

        // Launch 2, well past the clamped window: the pause is over, not re-imposed.
        ProviderGates.ResetForTests();
        ProviderGates.PathOverride = temp.Path;
        clock.Advance(Cap + TimeSpan.FromMinutes(1));
        ProviderGates.Clock = clock.Read;

        var decision = ProviderGates.For(ProviderIds.GoogleDict).TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, decision.Outcome);      // expired, so the first caller probes
    }

    [Fact]
    public void A_file_that_could_not_be_read_is_never_overwritten()
    {
        // The loss happens at the READ end, where none of the atomic-write machinery helps. A
        // sharing violation lasting the 50 ms of the session's first request would otherwise cost
        // the standing pause AND every preserved unknown id — AC 5 — because the next transition
        // rewrites the file from an empty registry. Not persisting this session is far cheaper.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        var original = OneProvider(ProviderIds.GoogleDict, Iso(clock.Now + TimeSpan.FromMinutes(5)));
        File.WriteAllText(temp.Path, original);

        // The lock lasts only as long as the read. Flushing while it is still held would prove
        // nothing — the OS would refuse the write on its own, whatever this code did.
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ProviderGates.For(ProviderIds.Edge).TryEnter(RequestPriority.Interactive);   // load fails

        // The file is perfectly writable again. The ONLY thing standing between it and an
        // overwrite from an empty registry is that this process never managed to read it.
        ProviderGates.For(ProviderIds.Edge).ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();

        Assert.Equal(original, File.ReadAllText(temp.Path));
    }

    [Fact]
    public void A_newer_builds_file_is_left_alone_rather_than_downgraded_over()
    {
        // The forward guard already yields an empty registry for a version this build cannot read
        // (TP-GATE-20). It must not also DELETE it: AC 5 preserves a newer build's unknown ids one
        // by one, and a schema bump is the very downgrade that makes preservation matter — losing
        // the whole file wholesale would defeat it exactly when it counts.
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;
        var newer = """{ "version": 2, "providers": { "google-dict": { "somethingElseEntirely": 1 } } }""";
        File.WriteAllText(temp.Path, newer);

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();

        Assert.Equal(newer, File.ReadAllText(temp.Path));
    }

    [Fact]
    public void An_unreachable_provider_does_not_rewrite_the_file_every_few_seconds()
    {
        // The churn the SoftCooldown exclusion was written to prevent, arriving through the cycle
        // the cooldown itself creates: the 5 s window elapses, a caller is granted a probe, the
        // probe times out, five seconds later again. §5.7 budgets "a handful per session".
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        var soft = TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs);

        var gate = ProviderGates.For(ProviderIds.GoogleGtx);
        gate.ReportFailure(TranslationErrorKind.Timeout);          // a provider that is simply down

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(soft + TimeSpan.FromSeconds(1));
            var probe = gate.TryEnter(RequestPriority.Interactive);
            Assert.Equal(GateOutcome.Probe, probe.Outcome);        // the cycle really does run
            gate.ReportFailure(TranslationErrorKind.Timeout, probeToken: probe.ProbeToken);
        }

        ProviderGates.Flush();
        Assert.False(File.Exists(temp.Path), "an outage is not state worth a single write");

        // …and the moment it comes back, that IS worth one: the edge onto Closed is always written.
        clock.Advance(soft + TimeSpan.FromSeconds(1));
        var last = gate.TryEnter(RequestPriority.Interactive);
        gate.ReportSuccess(last.ProbeToken);
        ProviderGates.Flush();
        Assert.True(File.Exists(temp.Path));
    }

    [Fact]
    public void A_probe_grant_alone_writes_nothing_because_it_changes_nothing()
    {
        // Open -> HalfOpen moves no field ProviderStateRecord carries: the probe latch is not
        // persisted (§5.7), so the record is identical either side of a grant. Winston's ruling 3,
        // "no save on a no-op" — the write was pure churn on the request path.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.Azure);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();
        var afterOpen = File.ReadAllText(temp.Path);
        File.Delete(temp.Path);

        clock.Advance(Base + TimeSpan.FromSeconds(1));
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
        ProviderGates.Flush();

        Assert.False(File.Exists(temp.Path));
        // …and had it written, it would have written exactly what was already there.
        ProviderStateStore.Save(temp.Path, new Dictionary<string, ProviderStateRecord>
        {
            [ProviderIds.Azure] = gate.ExportState()!,
        }, null);
        Assert.Equal(afterOpen, File.ReadAllText(temp.Path));
    }

    [Fact]
    public void The_debounce_timer_writes_without_anybody_calling_Flush()
    {
        // Every other case drives persistence through Flush(), so deleting the timer from
        // QueueSave leaves them all green — and the timer is the ONLY thing that persists state
        // while the app is running (OnClosing catches at most the last second). Bounded by a
        // deadline rather than a sleep: the assertion is "it happened", never "it took this long".
        using var temp = new TempGateState();
        ProviderGates.Clock = new FakeClock().Read;
        ProviderGates.SaveDebounceMs = 0;

        ProviderGates.For(ProviderIds.GoogleDict).ReportFailure(TranslationErrorKind.RateLimited);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(temp.Path) && DateTime.UtcNow < deadline) Thread.Sleep(5);

        Assert.True(File.Exists(temp.Path), "the debounce timer never fired: nothing persists mid-session");
    }

    // ---- TP-GATE-22: unknown ids -------------------------------------------------------------

    [Fact]
    public void An_unknown_provider_id_survives_a_rewrite_verbatim()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path, """
            { "version": 1, "providers": {
                "future-provider": { "blockedUntil": "2099-01-01T00:00:00+00:00", "strikes": 4,
                                     "somethingThisBuildNeverHeardOf": true } } }
            """);

        // A real transition on ANOTHER id rewrites the file.
        var gate = ProviderGates.For(ProviderIds.Edge);
        gate.TryEnter(RequestPriority.Interactive);                 // loads
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();

        using var doc = JsonDocument.Parse(File.ReadAllText(temp.Path));
        var future = doc.RootElement.GetProperty("providers").GetProperty("future-provider");

        Assert.Equal(4, future.GetProperty("strikes").GetInt32());
        Assert.Equal("2099-01-01T00:00:00+00:00", future.GetProperty("blockedUntil").GetString());
        // Verbatim, not re-serialised through this build's record: a field an older build does not
        // know about must not be dropped by it.
        Assert.True(future.GetProperty("somethingThisBuildNeverHeardOf").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("providers").TryGetProperty(ProviderIds.Edge, out _));
    }

    // ---- TP-GATE-23: transition-only, debounced, coalesced -------------------------------------

    [Fact]
    public void Fifty_refusals_on_an_open_gate_cost_zero_writes()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        ProviderGates.Flush();
        File.Delete(temp.Path);                                    // the transition's write, consumed

        for (var i = 0; i < 50; i++)
            Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        ProviderGates.Flush();

        // A 30-minute open window would otherwise write the file thousands of times: a refusal is
        // not a transition and queues nothing at all.
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public void A_soft_cooldown_and_a_cancel_queue_no_write()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.GoogleGtx);
        gate.ReportFailure(TranslationErrorKind.Timeout);          // a DNS blip is worth 5 seconds
        gate.ReportFailure(TranslationErrorKind.Network);
        gate.ReportFailure(TranslationErrorKind.Cancelled);        // TP-GATE-12: nothing at all
        ProviderGates.Flush();

        // Not persisted, and E2.S6 must make the same call for its log lines: a LIVE tick that
        // fails DNS every 700 ms may not write the file every 700 ms.
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public void Transitions_inside_the_debounce_window_coalesce_into_one_write()
    {
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        ProviderGates.SaveDebounceMs = 60_000;   // the seam, so the case proves coalescing without
                                                 // waiting a real second (CI-3: no Task.Delay)
        var gate = ProviderGates.For(ProviderIds.Azure);
        gate.ReportFailure(TranslationErrorKind.RateLimited);      // Closed → Open
        clock.Advance(Base + TimeSpan.FromSeconds(2));
        var probe = gate.TryEnter(RequestPriority.Interactive);    // Open → HalfOpen
        gate.ReportSuccess(probe.ProbeToken);                      // HalfOpen → Closed

        Assert.False(File.Exists(temp.Path));                      // three transitions, still nothing

        ProviderGates.Flush();
        Assert.True(File.Exists(temp.Path));                       // …one write, of the state NOW

        // …and that write emptied the queue: a second Flush has nothing left to do.
        File.Delete(temp.Path);
        ProviderGates.Flush();
        Assert.False(File.Exists(temp.Path));

        // The state at flush time is the closed one, not the open one that queued the first save.
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    [Fact]
    public void A_pending_save_never_survives_ResetForTests()
    {
        // IS-4's second half. Without it a write queued by one case lands during the next one — in
        // the next case's temp directory, or after PathOverride has gone back to the real file.
        var clock = new FakeClock();
        string path;
        using (var temp = new TempGateState())
        {
            path = temp.Path;
            ProviderGates.Clock = clock.Read;
            ProviderGates.SaveDebounceMs = 60_000;
            ProviderGates.For(ProviderIds.Bergamot).ReportFailure(TranslationErrorKind.RateLimited);

            ProviderGates.ResetForTests();
            ProviderGates.PathOverride = path;
            ProviderGates.Flush();

            Assert.False(File.Exists(path));
            Assert.Equal(1000, ProviderGates.SaveDebounceMs);      // the seam is reset too
        }
    }

    // ---- TP-START-02: nothing new on a startup path ---------------------------------------------

    [Fact]
    public void Nothing_reads_the_file_before_the_first_TryEnter()
    {
        // The Services-level half of TP-START-02 (the STA half needs the window and lands in E7).
        // The trigger CANNOT be For(id): the read chain is a field initializer
        // (MainWindow.xaml.cs:43) that runs inside the constructor, before first paint.
        using var temp = new TempGateState();
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        File.WriteAllText(temp.Path,
            OneProvider(ProviderIds.GoogleDict, Iso(clock.Now + TimeSpan.FromMinutes(5))));

        var gate = ProviderGates.For(ProviderIds.GoogleDict);      // construction: reads nothing
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        Assert.Equal(GateState.Closed, ProviderGates.All()[ProviderIds.GoogleDict].State);
        Assert.Null(ProviderGates.Snapshot(ProviderIds.Edge));

        var decision = gate.TryEnter(RequestPriority.Interactive); // …and this is what loads
        Assert.Equal(GateOutcome.Open, decision.Outcome);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(5), gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void No_startup_path_mentions_ProviderGates()
    {
        // The source scan this story owes (TP-START-02 / R-10): the way I10 breaks is invisible —
        // a convenience call added to the constructor by the next person, and the file is read
        // before first paint again. OnClosing's Flush() is the ONE permitted reference outside
        // Services/ (ruling E2-e) and is carved out explicitly.
        var root = RepoRoot();
        var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        foreach (var member in new[] { "public MainWindow()", "private void ApplySettings()",
                                       "private async void OnWindowLoaded(" })
            Assert.DoesNotContain("ProviderGates", Code(Body(main, member)));

        // …and the carve-out is not vacuous: the shutdown flush really is there.
        Assert.Contains("ProviderGates.Flush();",
            Code(Body(main, "protected override void OnClosing(")));

        // Nothing else in the app touches the registry either (E2-e). A later story that needs a
        // second call site — E6.S3 wires ClearAuthBlock to the key save — updates this list on
        // purpose, which is the point: the next reference must be a decision, not a convenience.
        var strays = ProductionSources(root)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("Services", StringComparison.Ordinal))
            .SelectMany(f => Code(File.ReadAllText(f))
                .Split('\n')
                .Where(l => l.Contains("ProviderGates", StringComparison.Ordinal))
                .Select(l => Path.GetFileName(f) + ": " + l.Trim()))
            .ToList();

        Assert.Equal(new[] { "MainWindow.xaml.cs: ProviderGates.Flush();" }, strays);
    }

    // ---- the %AppData% guard (T5) --------------------------------------------------------------

    [Fact]
    public void A_write_never_reaches_the_real_AppData_state_file()
    {
        // LoggingTests.The_test_run_never_writes_to_the_real_AppData_log, for the third file the
        // suite could poison. It asserts the real file is UNTOUCHED rather than absent: once this
        // ships, a developer running the app legitimately has one, and a suite that fails on their
        // machine is a suite they turn off.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PWRUHelper", "provider-state.json");
        var existed = File.Exists(real);
        var stamp = existed ? File.GetLastWriteTimeUtc(real) : default;

        using (var temp = new TempGateState())
        {
            ProviderGates.Clock = new FakeClock().Read;
            ProviderGates.For(ProviderIds.GoogleDict).ReportFailure(TranslationErrorKind.RateLimited);
            ProviderGates.Flush();

            Assert.True(File.Exists(temp.Path));
            Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(ProviderGates.StatePath));
            Assert.False(File.Exists(temp.Path + ".tmp"), "the temp file must be swapped in, not left behind");
        }

        Assert.Equal(existed, File.Exists(real));
        if (existed) Assert.Equal(stamp, File.GetLastWriteTimeUtc(real));
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Comments stripped, so a <c>///</c> line that names the registry to point the reader
    /// at it is prose and not a call — the same rule
    /// <c>ProviderGatesTests.Every_test_class_that_touches_the_registry_joins_this_collection</c>
    /// applies to the test tree.</summary>
    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    /// <summary>The body of the member whose signature starts at <paramref name="signature"/>, by
    /// brace depth from its opening brace. Crude on purpose: it has to be readable by the next
    /// person who moves a method, not general.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is no longer in MainWindow.xaml.cs — fix the scan, not the guard");

        var open = source.IndexOf('{', start);
        Assert.True(open > 0, $"'{signature}' has no body");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"'{signature}' has an unbalanced body");
        return "";
    }

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f)
                            .Split('/', '\\')
                            .SkipLast(1)
                            .All(seg => !seg.StartsWith('.')
                                        && !seg.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}
