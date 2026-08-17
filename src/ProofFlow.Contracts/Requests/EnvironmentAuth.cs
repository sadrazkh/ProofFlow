using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofFlow.Contracts.Requests;

/// <summary>
/// How an environment authenticates, configured once and applied to everything it sends.
///
/// This is the thing the product promised and did not have. A column called
/// <c>AuthenticationJson</c> has existed on <c>ProjectEnvironment</c> since the first migration,
/// with a comment saying authentication is applied to every request unless a step overrides it —
/// and nothing wrote it and nothing read it. So an API that needs a token could only be tested by
/// putting an <c>Authorization</c> header on every endpoint and every step by hand, and a token
/// pasted into a header is a suite that starts failing overnight for a reason that has nothing to
/// do with the API.
///
/// Four kinds, and the boundaries between them are what real APIs actually differ on rather than
/// what a specification enumerates:
///
/// <list type="bullet">
/// <item><see cref="AuthMode.None"/> — most of the internet.</item>
/// <item><see cref="AuthMode.Header"/> — one fixed header. A long-lived bearer token and an API key
/// are the same thing twice: <c>Authorization: Bearer x</c> and <c>X-Api-Key: x</c> differ only in
/// two strings, and offering them as two options would be offering the same option twice.</item>
/// <item><see cref="AuthMode.SignIn"/> — send credentials somewhere, get a token back. The common
/// case for anything written in-house, and the one that was impossible.</item>
/// <item><see cref="AuthMode.OAuth2"/> — the form grants, which already worked in the request lab
/// and are moved here rather than rewritten.</item>
/// </list>
///
/// Values may be <c>{{secrets.apiPassword}}</c> anywhere, and are meant to be: the flow that writes
/// this seals the credentials and stores references, so this record can be read by anybody who can
/// read the environment without that telling them a password.
/// </summary>
public sealed record EnvironmentAuth
{
    public AuthMode Mode { get; init; } = AuthMode.None;

    // ---- Header ---------------------------------------------------------------------------------

    /// <summary>The header name. <c>Authorization</c> for a bearer token, anything for an API key.</summary>
    public string? HeaderName { get; init; }

    /// <summary>
    /// The whole header value, including any prefix.
    ///
    /// Not a token plus a separately configured «Bearer» — because the prefix is where this goes
    /// wrong. Some APIs want «Bearer x», some want «Token x», one popular one wants just «x», and a
    /// field called Token with a checkbox called «prefix with Bearer» cannot express the second.
    /// </summary>
    public string? HeaderValue { get; init; }

    // ---- SignIn and OAuth2 ----------------------------------------------------------------------

    /// <summary>Where to sign in. Relative to the base URL, or absolute for a separate auth host.</summary>
    public string? TokenUrl { get; init; }

    public string Method { get; init; } = "POST";

    /// <summary>
    /// <c>json</c> or <c>form</c>. JSON is the default because it is what an in-house login takes,
    /// and sending form-encoded to one is the 415 that made this look broken.
    /// </summary>
    public string BodyKind { get; init; } = "json";

    /// <summary>
    /// The credentials, as the field names the server expects.
    ///
    /// A dictionary rather than Username and Password, because the names are the server's business:
    /// «email», «userName», «login», «user» and «j_username» are all in production somewhere, and a
    /// field labelled Username that sends <c>username</c> is a field that cannot talk to four of
    /// those five.
    /// </summary>
    public IReadOnlyDictionary<string, string> Credentials { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Where the token is in the response, as a dotted path — «accessToken», «data.token».
    ///
    /// Null means look for it: the reader tries the four names specifications and real servers use.
    /// Naming it is for the server that calls it something else entirely.
    /// </summary>
    public string? TokenPath { get; init; }

    /// <summary>
    /// Where the expiry is, in seconds, as a dotted path — «expires_in».
    ///
    /// Null means assume <see cref="DefaultLifetimeSeconds"/>. Getting this wrong is cheap in one
    /// direction and not the other: too short re-signs in more often than needed, too long sends a
    /// dead token and reads the 401 as a regression.
    /// </summary>
    public string? ExpiresInPath { get; init; }

    /// <summary>
    /// How the token is sent afterwards. <c>{token}</c> is replaced.
    ///
    /// A template rather than a boolean, for the same reason as <see cref="HeaderValue"/>.
    /// </summary>
    public string UseHeaderName { get; init; } = "Authorization";

    public string UseHeaderTemplate { get; init; } = "Bearer {token}";

    // ---- OAuth2 only ----------------------------------------------------------------------------

    /// <summary><c>client_credentials</c> or <c>password</c>.</summary>
    public string? Grant { get; init; }

    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }

    /// <summary>
    /// Whether the client id and secret go in an Authorization header rather than the form.
    ///
    /// Both are in the specification and servers disagree about which they accept, so this is a
    /// choice rather than a default: guessing wrong produces a 401 that says nothing about which
    /// half was wrong.
    /// </summary>
    public bool CredentialsInHeader { get; init; }

    // ---- reading and writing --------------------------------------------------------------------

    /// <summary>Fifteen minutes. Short enough to be wrong safely, long enough not to sign in per
    /// request during a sweep of two thousand rows.</summary>
    public const int DefaultLifetimeSeconds = 900;

    public bool SendsAnything => Mode is not AuthMode.None;

    /// <summary>Whether using this means asking a server for something first.</summary>
    public bool NeedsToken => Mode is AuthMode.SignIn or AuthMode.OAuth2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads what is stored, and treats anything unreadable as «no authentication».
    ///
    /// Never throws. An environment with a malformed blob in this column has to stay openable —
    /// otherwise a bad value makes the page that would let somebody fix it the page that will not
    /// load. What it means is that a run against it fails with the API's own 401, which is a
    /// message about the right subject.
    /// </summary>
    public static EnvironmentAuth Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new EnvironmentAuth();

        try
        {
            return JsonSerializer.Deserialize<EnvironmentAuth>(json, Json) ?? new EnvironmentAuth();
        }
        catch (JsonException)
        {
            return new EnvironmentAuth();
        }
    }
}

public enum AuthMode
{
    None = 0,

    /// <summary>One fixed header: a long-lived token, or an API key.</summary>
    Header = 1,

    /// <summary>Send credentials to an address and read a token out of the answer.</summary>
    SignIn = 2,

    /// <summary>An OAuth2 form grant.</summary>
    OAuth2 = 3,
}
