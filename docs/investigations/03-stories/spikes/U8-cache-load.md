# U8 — what a full translation cache costs to load, save and hold

_Spike E4.S3 (release A.2, epic E4) · measured 2026-09-07 on the dev box by Amelia · harness:
`tests/PWRUHelper.Tests/CacheLoadSpike.cs`, opt-in, invisible to a normal `dotnet test`._

**Verdict in one sentence: capacity 2000 stands — a full file loads in 17.8 ms off the UI thread for
+960 KB of managed heap — but the same measurement found the file itself weighs 981 KB, not §8.2's
≈300 KB, so the 1 MB read bound that would have silently refused it was raised to 4 MB.**

## 1. Machine

| Field | Value |
|---|---|
| Host / OS | `GSIN-WS-DT004`, Windows 11 build 26200, x64 |
| CPU / RAM | Intel Core i7-9700 (8 cores), 16 GB |
| Disk under test | `%TEMP%` on the KIOXIA KBG30ZMV512G NVMe SSD (system disk) |
| Runtime | .NET 8.0.30, Debug build of the test assembly |
| AV posture | Microsoft Defender running, no exclusion for `%TEMP%`; **this is the corporate dev box, not the Defender-only personal machine AC 1 also asks for** |
| Warmth | **warm** — every file was written by the same process seconds before it was read, so it is in the OS file cache |

## 2. Method

Each file is generated as **realistic** entries, never repeated filler: Russian chat lines of 20–90
characters (68 on average) composed from a 24-phrase pool with their English translations, two
thirds `ru|en` keys and one third `auto|en`, `"p"` populated across `google-dict` / `google-gtx` /
`deepl` / empty. The file is written **through the store's own save path** (`Store` × N, then
`SaveNow`), so the schema, the field names, the invariant timestamps and the MRU order are the
shipped ones. Every number is the **median of 7 timed runs after a discarded warm-up**; the load is
timed around a fresh store's first `TryGet` **miss**, which is the trigger E4.S2 built. Managed heap
is `GC.GetTotalMemory(forceFullCollection: true)` either side of the load with the store rooted;
working set is `Process.WorkingSet64` and is the noisy one — it is a whole-process number and at
5000 entries it read as −16 KB, which means "lost in the noise", not "free".

## 3. The curve

| entries | file | B/entry | ctor | load (median / min / max) | save | managed Δ | working-set Δ |
|---|---|---|---|---|---|---|---|
| 500 | 231 KB | 474 | 0.001 ms | **3.6 ms** / 3.5 / 3.8 | 11.9 ms | +229 KB | +644 KB |
| **2000** | **981 KB** | **502** | **0.003 ms** | **17.8 ms** / 14.6 / 21.0 | **15.3 ms** | **+960 KB** | +892 KB |
| 5000 | 2 612 KB | 535 | 0.006 ms | 32.0 ms / 25.4 / 34.0 | 28.9 ms | +2 514 KB | ~0 (noise) |

_Addendum — the same table after E4.S5's encoder change is in §8._

Three more numbers from the same run:

- **The pre-sized map costs nothing.** Constructing the 2000-slot store — the one new byte cost E4.S4
  put before first paint — is **3 µs**, and in the whole startup shape, building all three chains over
  a full file is **6.5 ms for 97 KB**, with **0 entries read** (§5).
- **The load scales linearly**, at ≈55 MB/s of JSON including the allocation of two strings per entry.
- **Saving a full store is 15 ms**, which settles the *cost* of the debounced write but not the length
  of its window (still `[ASSUMED]`).

## 4. The finding nobody was looking for: 502 B an entry, not 150

§8.2 estimated ~150 B an entry and therefore ≈300 KB for a full 2000. A realistic file measured
**502 B an entry and 981 KB** — 3.3× the estimate — for a reason that is one line of code:
`TranslationCacheStore.Options` uses `JsonSerializer`'s **default encoder**, which escapes every
non-ASCII character as `\uXXXX`. Cyrillic is 2 bytes a character in UTF-8 and **6** once escaped, so
a 68-character Russian line occupies 408 bytes in the file. The estimate counted the UTF-8 bytes.

The consequence was live in the shipped design: `MaxBytes` refused any file over **1 MB unread**, and
a full cache at capacity 2000 weighed 981 KB — **4% of headroom**, crossing at ≈**2088 entries**. The
heaviest users — the ones the cache exists for — would have earned a full cache and then had it
silently dropped on every launch, with no error, no log line and no fallback. This is E4.S2's own
deferred review finding ("a byte bound over a COUNT-bounded capacity"), and it was real.

**Fixed by the bound, not by the capacity.** `MaxBytes` is now **4 MB**: 4× a measured full cache,
still an unmistakable refusal for the corrupt 400 MB file it exists to stop, and ≈70 ms of parsing at
the rate measured above. The demonstration is in the harness — a file one entry over the bound loads
**0** entries. The capacity was left alone because it is the feature; the bound is the guard, and it
was the guard that was mis-sized.

**Not done here, on purpose:** switching to a non-escaping encoder would divide the Cyrillic by three
and is the obvious follow-up, but it changes the bytes of a file users already have and the spike's
own terms forbade touching the format. The 4 MB bound is sized so that change stays an optimisation.
**It was done next door — see the addendum in §8.**

## 5. AC 2 — nothing is read before it is asked for

With a full 2000-entry file in place, building the read, read-once and write chains (what
`MainWindow`'s constructor does) leaves `TranslationChains.Cache.Count` at **0**: constructed, not
read. TP-CACHE-10 and TP-CACHE-11 make the same claim in CI; this makes it on the path the window
actually walks, over a file that is there and full. The Process Monitor confirmation on a real launch
belongs with the owner's hand-off below.

## 6. Go / no-go

**Go.** §8.2's criterion was ≤ 50 ms off the UI thread and a working-set delta small against the
~150 MB budget. A full 2000-entry cache loads in **17.8 ms** on the calling thread of the **first
miss** — inside an already-awaited translation, never before first paint — for **≈1 MB**, i.e. **0.6%
of the budget**. Capacity **2000 stands**, its grade in `TranslationPolicy.cs` moves from `[ASSUMED]`
to `[MEASURED]`, and the knob §8.2 offered was not needed.

Two cautions worth writing down rather than discovering later:

1. **These are warm numbers.** A cold, Defender-scanned first read is the half this box cannot
   produce — it is the P1 machine class, and it is the owner's hand-off.
2. **5000 would not have passed.** At 32 ms warm it is already two thirds of the criterion before any
   cold-start multiplier, and it costs 2.5 MB. If the capacity is ever raised, it is re-measured
   first — the same sentence §8.2 wrote about lowering it.

## 7. The owner's half of AC 1 (does **not** gate A.2)

From a checkout of this branch, one line in `cmd`:

```
set PWRU_SPIKE=1 && dotnet test tests/PWRUHelper.Tests --filter Category=Spike --logger "console;verbosity=detailed"
```

Run it **once right after a reboot** (cold file cache, Defender cold) and once again straight after
(warm), on the personal Defender-only machine, and paste the two tables here. What matters is the
2000-entry `load ms` column: anything under ~50 ms confirms the capacity for the machine class this
project is actually fighting. Without the environment variable the harness compiles but no case is
discovered, so a normal `dotnet test` is unchanged — 774 tests since E4.S5, ~2 s.

## 8. Addendum — re-measured after E4.S5 (2026-09-07, same box, same harness, same seed)

E4.S5 took the follow-up §4 named: `TranslationCacheStore.Options` now writes with
`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, so Cyrillic goes into the file as UTF-8 instead of
`\uXXXX`. Nothing else changed — same schema, same generator, same `PWRU_SPIKE=1` run.

| entries | file | B/entry | load (median) | vs. §3 |
|---|---|---|---|---|
| 500 | 128 KB | 263 | 2.36 ms | 231 KB / 474 / 3.6 ms |
| **2000** | **541 KB** (554 233 B) | **277** | **9.73 ms** | 981 KB / 502 / 17.8 ms |
| 5000 | 1 430 KB | 293 | 21.99 ms | 2 612 KB / 535 / 32.0 ms |

**A full cache is 45% smaller and loads in 55% of the time**, and the file that forced `MaxBytes` up
now fits inside the 1 MB bound it broke. It is **not** §8.2's ~150 B an entry, and the reason is
worth recording so nobody re-opens this: only the Russian **key** was ever escaped. The English value
(~70 B), the 33-byte round-trip timestamp and the field names are ~130 B of every row, and no encoder
touches them — ~150 B/entry was never reachable with this schema.

`MaxBytes` stays at **4 MB**: the bound guards against a corrupt or hand-edited file, not against the
cache, and it is now ×7.6 a full one (crossing at ≈**15 135** entries). The number CI defends is
`TranslationCachePersistenceTests.A_full_realistic_cache_costs_under_300_bytes_an_entry`, which runs
over **this** harness's `Entries(2000)` — the generator is `internal` for exactly that reason, so the
number in this table and the number in the suite cannot drift.
