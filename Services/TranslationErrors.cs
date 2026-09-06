namespace PWRUHelper.Services;

/// <summary>
/// The error vocabulary every layer of the translation path speaks. It exists so the message a
/// player sees and the decision the code takes stop being derived from a sentence: a 429 and a
/// bot-block page both used to read as "unexpected response", and nothing could tell them apart.
/// </summary>
public enum TranslationErrorKind
{
    RateLimited,        // 429, or a Google "automated queries" HTML page on any status
    Blocked,            // 403 with no key, a captcha/interstitial page, an abuse block
    Unavailable,        // 5xx
    Timeout,            // HttpClient timeout (an OCE with ct NOT cancelled)
    Network,            // DNS, TLS, connect, proxy — HttpRequestException
    BadResponse,        // unparseable body, wrong shape, batch count mismatch on a 1:1 provider
    QuotaExhausted,     // DeepL 456, Azure 403 with an out-of-quota envelope
    AuthFailed,         // 401, or 403 while a key was sent
    Cancelled,          // a genuine user cancel — see the rule below
    AllProvidersPaused, // every tier in the chain was gate-open or failed
    Unknown,            // the mapper's last resort; must never become common
}

// The Cancelled contract, verbatim from the architecture (§4.1): Kind.Cancelled exists so the
// mapper is a total function, but a provider that recognises a genuine cancellation RETHROWS
// the OperationCanceledException — it never wraps it. A TranslationException with Kind.Cancelled
// must never be constructed. Wrapping one would turn every HttpClient timeout (an OCE whose token
// is NOT cancelled) into a phantom user-cancel: the fallback stops firing and the LIVE indicator
// is left running with nothing behind it. TranslationErrorsTests scans the sources to keep this true.

/// <summary>A translation problem worth showing to the user in plain language, carrying the
/// machine-readable <see cref="TranslationErrorKind"/> behind that sentence.</summary>
public class TranslationException : Exception
{
    public TranslationErrorKind Kind { get; }

    /// <summary>When the caller may try again — from a Retry-After header or the provider gate.
    /// Null when nothing said. Rendered as a countdown by the UI; Services/ never formats it.</summary>
    public DateTimeOffset? RetryAt { get; }

    /// <summary>Which provider produced the failure. For the diagnostic log only — never shown.</summary>
    public string? ProviderId { get; }

    // No message-only constructor, deliberately: it is what forces every throw site to state a Kind.
    public TranslationException(TranslationErrorKind kind, string message,
        DateTimeOffset? retryAt = null, string? providerId = null) : base(message)
    {
        Kind = kind;
        RetryAt = retryAt;
        ProviderId = providerId;
    }
}
