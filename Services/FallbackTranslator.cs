namespace PWRUHelper.Services;

/// <summary>
/// Tries a primary translator and, if it fails, transparently falls back to a secondary one.
/// Used to put DeepL in front of Google: if DeepL is misconfigured, rate-limited, out of quota
/// or times out, translations keep working on the free Google endpoint instead of erroring.
/// A real cancellation (the caller's token) is never swallowed — but a timeout is NOT a real
/// cancellation: on .NET 8 an HttpClient timeout surfaces as a TaskCanceledException with the
/// caller's ct NOT cancelled, so we let those fall through to the fallback like any other failure.
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logging.Warn("Primary translator failed, using fallback: " + ex.Message);
            return await _fallback.TranslateAsync(text, source, target, ct);
        }
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        try { return await _primary.TranslateLinesAsync(lines, source, target, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logging.Warn("Primary translator failed, using fallback: " + ex.Message);
            return await _fallback.TranslateLinesAsync(lines, source, target, ct);
        }
    }
}
