namespace ProofFlow.TestEngine.Http;

/// <summary>
/// A request as the engine describes it, before any variable has been resolved.
///
/// Deliberately not <c>HttpRequestMessage</c>: this shape is saved, versioned, exported and diffed,
/// and it has to survive round-tripping through JSON. It is also what the request builder edits.
/// </summary>
public sealed record HttpRequestDefinition
{
    public string Method { get; init; } = "GET";

    /// <summary>May be relative — it is joined onto the environment's base URL — and may contain
    /// <c>{{…}}</c> placeholders anywhere.</summary>
    public string Url { get; init; } = string.Empty;

    public IReadOnlyList<KeyValueEntry> Query { get; init; } = [];

    public IReadOnlyList<KeyValueEntry> Headers { get; init; } = [];

    public RequestBody? Body { get; init; }

    public AuthenticationSpec? Authentication { get; init; }

    /// <summary>Overrides the environment's timeout when set, and is clamped to it.</summary>
    public int? TimeoutSeconds { get; init; }

    public RetryPolicy Retry { get; init; } = RetryPolicy.None;
}

/// <summary>
/// A header, query parameter or form field. <see cref="Enabled"/> exists so the builder can keep a
/// row someone is experimenting with without sending it — deleting and retyping a header is how
/// people lose the one that mattered.
/// </summary>
public sealed record KeyValueEntry(string Name, string Value, bool Enabled = true, string? Description = null);

public sealed record RequestBody
{
    public BodyKind Kind { get; init; } = BodyKind.None;

    /// <summary>Raw text for Json, Text and Xml. Ignored for Form and None.</summary>
    public string? Content { get; init; }

    public IReadOnlyList<KeyValueEntry> Form { get; init; } = [];

    /// <summary>Overrides the content type the kind implies.</summary>
    public string? ContentType { get; init; }
}

public enum BodyKind
{
    None = 0,
    Json = 1,
    Text = 2,
    Xml = 3,
    FormUrlEncoded = 4,
    Multipart = 5,
    GraphQl = 6,
}

/// <summary>
/// How to authenticate. Values are <c>{{secrets.name}}</c> references rather than literals — a
/// literal here would be exported, logged and shown in a diff.
/// </summary>
public sealed record AuthenticationSpec
{
    public AuthenticationKind Kind { get; init; } = AuthenticationKind.None;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }
    public string? HeaderName { get; init; }
    public string? ApiKey { get; init; }
    public ApiKeyLocation KeyLocation { get; init; } = ApiKeyLocation.Header;
}

public enum AuthenticationKind
{
    /// <summary>Use whatever the environment defines. The default for a step.</summary>
    Inherit = 0,
    None = 1,
    Basic = 2,
    Bearer = 3,
    ApiKey = 4,
    OAuth2 = 5,
}

public enum ApiKeyLocation
{
    Header = 0,
    Query = 1,
}

public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 1;
    public int DelayMilliseconds { get; init; } = 500;
    public bool ExponentialBackoff { get; init; } = true;

    /// <summary>Status codes worth retrying. Empty means "only transport failures".</summary>
    public IReadOnlyList<int> RetryOnStatus { get; init; } = [];

    public static RetryPolicy None { get; } = new();
}

/// <summary>
/// What came back, and everything needed to explain it afterwards.
///
/// <see cref="Body"/> is a string rather than a stream: it has already been read, bounded by the
/// environment's size cap, because the engine compares it, hashes it and stores it. Streaming would
/// buy nothing when every consumer needs the whole thing.
/// </summary>
public sealed record HttpExchangeResult
{
    public required string ResolvedUrl { get; init; }
    public required string Method { get; init; }
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValueEntry> ResponseHeaders { get; init; } = [];
    public string Body { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public long BodyBytes { get; init; }

    /// <summary>Wall-clock time from the first byte sent to the last byte read.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Every address this request was redirected through, in order.</summary>
    public IReadOnlyList<string> RedirectChain { get; init; } = [];

    /// <summary>How many attempts it took. Greater than one means a retry hid an earlier failure,
    /// which the report has to show rather than smooth over.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>The request as actually sent, with secrets already redacted.</summary>
    public IReadOnlyList<KeyValueEntry> SentHeaders { get; init; } = [];

    public string? SentBody { get; init; }

    /// <summary>Set when the exchange never produced a response. Everything above is then empty.</summary>
    public HttpFailure? Failure { get; init; }

    public bool Succeeded => Failure is null;

    /// <summary>True when the body parsed as JSON. Drives which viewer the interface offers.</summary>
    public bool IsJson => ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Why no response arrived, in terms a non-specialist can act on.
///
/// The brief forbids showing a raw stack trace, and the exception message is often no better:
/// "The SSL connection could not be established" does not tell anyone that the certificate is
/// self-signed and there is a switch for that.
/// </summary>
public sealed record HttpFailure(HttpFailureKind Kind, string Message, string? Detail = null);

public enum HttpFailureKind
{
    Refused,
    Timeout,
    DnsFailure,
    Certificate,
    TooManyRedirects,
    ResponseTooLarge,
    BlockedByPolicy,
    Cancelled,
    Unknown,
}

/// <summary>
/// Sends one request. The engine's only way out to the network.
///
/// A port rather than an <c>HttpClient</c> so the engine can be unit-tested against a scripted
/// transport, and so a request can later be handed to a runner agent inside a private network
/// without any node handler knowing the difference.
/// </summary>
public interface IHttpExecutor
{
    Task<HttpExchangeResult> SendAsync(
        HttpRequestDefinition request,
        UrlPolicy policy,
        CancellationToken cancellationToken = default);
}
