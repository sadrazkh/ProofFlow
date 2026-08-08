using Microsoft.Extensions.Options;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Where this installation lives, as far as the outside world is concerned.
///
/// This exists because of one specific attack. A link in an email is built from a host name, and the
/// obvious source of a host name is the request that asked for the link — but the Host header is
/// attacker-controlled. Somebody types a victim's address into the password-reset form with a
/// forged Host, and the victim receives a genuine email from us containing a reset link pointing at
/// the attacker's server. It is called reset poisoning and it works because everything about the
/// message is real except the domain.
///
/// So the address is configuration, not input. When it is not configured — a single-machine install,
/// a developer's laptop — the request is used, and <c>AllowedHosts</c> is what keeps that honest.
/// </summary>
public sealed class PublicLinks(IOptions<PublicAddress> address, IHttpContextAccessor accessor)
{
    private readonly string? _configured = address.Value.PublicUrl?.TrimEnd('/');

    /// <summary>Whether the address came from configuration rather than from the request.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configured);

    /// <summary>An absolute URL for a path inside this application.</summary>
    public string Absolute(string path)
    {
        var tail = path.StartsWith('/') ? path : "/" + path;

        if (_configured is { Length: > 0 }) return _configured + tail;

        var request = accessor.HttpContext?.Request;

        return request is null
            ? tail
            : $"{request.Scheme}://{request.Host}{tail}";
    }
}

/// <summary>The one setting: the address people type to reach this installation.</summary>
public sealed class PublicAddress
{
    public const string Section = "App";

    /// <summary>
    /// For example <c>https://proofflow.example.com</c>. Empty on a laptop, required behind a proxy
    /// if any link is ever emailed.
    /// </summary>
    public string? PublicUrl { get; set; }
}
