namespace PWRUHelper.Services;

/// <summary>
/// The bounded LRU that <see cref="CachingTranslator"/> used to keep inside itself — a
/// <c>string → string</c> map with an eviction order, and deliberately nothing more. It moved out
/// (E4.S1) for one reason: a decorator can only wrap one inner translator, so "one shared cache
/// behind the read chain, the read-once chain and the write chain" (§8.2, decision F) has to be one
/// shared <b>store</b> behind three thin decorators. E4.S2 gives it a file; E4.S4 hands the same
/// instance to all three.
///
/// <para><b>What it does not know.</b> The key format — <c>source|target|text.Trim()</c> — and the
/// rule that failure placeholders (anything starting with <c>(</c>, I4) are never cached both stay in
/// <see cref="CachingTranslator"/> (§3.1's responsibility table). That is not tidiness: the "(" rule
/// upstream of one shared store cannot be forgotten by one of three decorators, whereas the same rule
/// duplicated in three places can. Handed a placeholder directly, this class will store it.</para>
///
/// <para><b>The order is the file format.</b> The front of <see cref="_order"/> is the
/// most-recently-used entry and the back is the one eviction takes; E4.S2 persists the list MRU-first
/// so the order survives a restart. An <c>AddLast</c> where this says <c>AddFirst</c> passes every
/// fresh-store test and evicts the wrong half of a long session's cache.</para>
///
/// <para>UI-free and I/O-free (I2, I10): one lock, two collections, no file until E4.S2 — the LIVE
/// loop reads it on a background thread while a click writes to it on the UI thread.</para>
/// </summary>
internal sealed class TranslationCacheStore
{
    private readonly int _capacity;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> _map;
    private readonly LinkedList<KeyValuePair<string, string>> _order = new();   // front = most-recently-used

    /// <summary>Capacity defaults to §8.2's 2000; the legacy <see cref="CachingTranslator"/>
    /// constructor still passes its own 500 (<see cref="TranslationPolicy.CacheCapacityToday"/>), so
    /// this story raises the number for the shared store and for nobody else.</summary>
    internal TranslationCacheStore(int capacity = TranslationPolicy.CacheCapacity)
    {
        _capacity = Math.Max(1, capacity);   // a zero would make every store a no-op
        _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, string>>>(_capacity);
    }

    /// <summary>Entries held right now. Exists so tests assert on the store instead of reaching for
    /// <c>_map</c> by reflection — a test that does that breaks on the next refactor.</summary>
    internal int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>A hit also promotes: reading an entry makes it the most-recently-used one, which is
    /// what makes this an LRU rather than a first-in-first-out queue. A miss yields <c>""</c>, never
    /// null.</summary>
    internal bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);        // touch → most-recently-used
                value = node.Value.Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    /// <summary>Stores or overwrites, promotes the entry to most-recently-used, and evicts the tail
    /// once the capacity is exceeded.</summary>
    internal void Store(string key, string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new KeyValuePair<string, string>(key, value);
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<KeyValuePair<string, string>>(new KeyValuePair<string, string>(key, value));
            _order.AddFirst(node);
            _map[key] = node;

            if (_map.Count > _capacity)
            {
                var lru = _order.Last!;       // least-recently-used
                _order.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }
}
