namespace PWRUHelper.Services;

/// <summary>
/// An <see cref="ITranslator"/> that remembers recent results so identical text is never
/// re-translated. On the RU server players spam the same LFM/greeting lines constantly and the
/// live OCR loop re-reads the same messages frame after frame, so this both makes the UI feel
/// instant and cuts calls to the translation backend (far fewer 429 rate-limits).
///
/// It wraps ANY inner translator, so the cache benefits whatever backend is active (Google
/// today, DeepL later). The cache is a bounded LRU — at most <c>capacity</c> entries, the
/// least-recently-used evicted first — so memory stays flat over a long session. Only genuine
/// successes are cached; the inner translator's failure placeholders (which start with "(")
/// are never stored, so a transient rate-limit can't get stuck on screen forever.
/// </summary>
public class CachingTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly int _capacity;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> _map;
    private readonly LinkedList<KeyValuePair<string, string>> _order = new();   // front = most-recently-used

    public CachingTranslator(ITranslator inner, int capacity = 500)
    {
        _inner = inner;
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, string>>>(_capacity);
    }

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        var key = Key(source, target, text);
        if (TryGet(key, out var cached)) return cached;

        var result = await _inner.TranslateAsync(text, source, target, ct);
        if (IsCacheable(text, result)) Store(key, result);
        return result;
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        // Serve the lines already known from cache; only ask the inner translator for the
        // misses, then splice results back into their original positions.
        var result = new string[lines.Count];
        var missIndexes = new List<int>();
        var missLines = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (TryGet(Key(source, target, lines[i]), out var cached)) result[i] = cached;
            else { missIndexes.Add(i); missLines.Add(lines[i]); }
        }

        if (missLines.Count > 0)
        {
            var fresh = await _inner.TranslateLinesAsync(missLines, source, target, ct);
            bool aligned = fresh.Count == missLines.Count;   // inner contract, but stay safe
            for (int j = 0; j < missIndexes.Count; j++)
            {
                var value = j < fresh.Count ? fresh[j] : missLines[j];   // never leave a null slot
                result[missIndexes[j]] = value;
                if (aligned && IsCacheable(missLines[j], value))
                    Store(Key(source, target, missLines[j]), value);
            }
        }
        return result.ToList();
    }

    // ----- cache internals -----

    private const string Sep = "|";   // language codes are [a-z]+, so a pipe can never collide

    private static string Key(string source, string target, string text)
        // Trim so leading/trailing-whitespace variants collapse to one entry (the backend trims
        // anyway); include source+target so "ru→en" and "ru→fr" of the same text stay distinct.
        => source + Sep + target + Sep + text.Trim();

    // Don't cache empty input, and never cache the inner translator's failure placeholders
    // ("(translation failed…)", "(rate-limited…)", "(skipped…)") — all of which start with "(".
    private static bool IsCacheable(string source, string value)
        => !string.IsNullOrWhiteSpace(source)
           && !string.IsNullOrEmpty(value)
           && !value.StartsWith('(');

    private bool TryGet(string key, out string value)
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

    private void Store(string key, string value)
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
