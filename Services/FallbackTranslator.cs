namespace PWRUHelper.Services;

/// <summary>
/// Tries a primary translator and, if it fails, transparently falls back to a secondary one.
/// Used to put DeepL in front of Google: if DeepL is misconfigured, rate-limited or out of
/// quota, translations keep working on the free Google endpoint instead of erroring. A real
/// cancellation is never swallowed — only genuine failures trigger the fallback.
/// </summary>
public class FallbackTranslator : ITranslator
{
    private readonly ITranslator _primary;
    private readonly ITranslator _fallback;

    public FallbackTranslator(ITranslator primary, ITranslator fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        try { return await _primary.TranslateAsync(text, source, target, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return await _fallback.TranslateAsync(text, source, target, ct); }
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        try { return await _primary.TranslateLinesAsync(lines, source, target, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return await _fallback.TranslateLinesAsync(lines, source, target, ct); }
    }
}
