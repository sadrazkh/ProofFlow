using System.Net;
using System.Net.Sockets;

namespace ProofFlow.TestEngine.Http;

/// <summary>
/// Decides whether ProofFlow is allowed to make a request.
///
/// This is the sharpest edge in the product. A user types a URL and the server fetches it, which is
/// the definition of server-side request forgery — and on a hosted installation the most valuable
/// thing reachable from the server is not the customer's API, it is the cloud metadata endpoint
/// that hands out the instance's credentials.
///
/// Two checks, and both are needed:
///
/// <see cref="Inspect"/> runs before the request, on the URL as written. It catches the obvious
/// cases and gives the person a message they can act on.
///
/// <see cref="IsAddressAllowed"/> runs at connect time, on the address the socket is about to
/// connect to. This is the one that actually holds. Checking only the hostname is defeated by a
/// name that resolves to a public address on the first lookup and a private one on the second
/// (DNS rebinding), and by any name an attacker controls that simply points at 169.254.169.254.
/// A hostname is a request; an address is what happens.
/// </summary>
public sealed class UrlGuard(UrlPolicy policy)
{
    public UrlPolicy Policy { get; } = policy;

    /// <summary>Checks a URL before it is used. Returns why it was refused, or null to proceed.</summary>
    public UrlRefusal? Inspect(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new UrlRefusal(UrlRefusalReason.Malformed, "The address is empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new UrlRefusal(UrlRefusalReason.Malformed,
                $"«{Trim(url)}» is not a complete address. It needs a scheme, like https://.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new UrlRefusal(UrlRefusalReason.Scheme,
                $"ProofFlow only makes http and https requests, not {uri.Scheme}.");

        // Credentials in the URL end up in logs, in run reports and in exported files, where they
        // are no longer credentials.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new UrlRefusal(UrlRefusalReason.Credentials,
                "Put the username and password in an authentication step, not in the address — " +
                "an address is recorded in the run report.");

        var host = uri.DnsSafeHost;

        if (Policy.DeniedHosts.Any(pattern => HostMatches(host, pattern)))
            return new UrlRefusal(UrlRefusalReason.HostDenied,
                $"{host} is on this environment's denied list.");

        if (Policy.AllowedHosts.Count > 0 && !Policy.AllowedHosts.Any(pattern => HostMatches(host, pattern)))
            return new UrlRefusal(UrlRefusalReason.HostNotAllowed,
                $"{host} is not on this environment's allowed list. Add it in the environment settings.");

        // A literal address skips DNS entirely, so judge it here as well as at connect time — the
        // message is far more useful before the request than after a socket refusal.
        if (IPAddress.TryParse(host, out var literal) && !IsAddressAllowed(literal))
            return new UrlRefusal(UrlRefusalReason.PrivateAddress, Explain(literal));

        return null;
    }

    /// <summary>
    /// The check that runs at connect time, against the address actually being dialled — including
    /// after every redirect, because a redirect is a second request to a second address.
    /// </summary>
    public bool IsAddressAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:127.0.0.1 is 127.0.0.1 wearing a hat. Unwrap before judging, or every v4 rule
        // below is bypassed by writing the address in its v6-mapped form.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IsMetadataAddress(address)) return false;

        return Policy.AllowPrivateNetwork || !IsPrivate(address);
    }

    /// <summary>Why an address was refused, in a sentence a non-specialist can act on.</summary>
    public string Explain(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IsMetadataAddress(address))
            return $"{address} is a cloud metadata address. ProofFlow never connects to those, " +
                   "because they hand out this server's own credentials.";

        return $"{address} is on a private or local network. Turn on «reach private networks» in " +
               "this environment's settings if that is deliberate.";
    }

    /// <summary>
    /// The link-local address every major cloud serves instance credentials from, plus its IPv6
    /// counterparts.
    ///
    /// Refused even when private networking is allowed. Someone testing an API on their own LAN has
    /// a real reason to reach 10.0.0.0/8; nobody has a reason to make ProofFlow read its own
    /// instance role credentials, and the one person who wants to is not the operator.
    /// </summary>
    private static bool IsMetadataAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 169.254.169.254 (AWS, Azure, GCP, DigitalOcean) and 169.254.170.2 (ECS task roles).
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // Alibaba Cloud and a few others serve metadata over 100.100.100.200.
            if (bytes is [100, 100, 100, 200]) return true;
            return false;
        }

        var text = address.ToString();
        return text is "fd00:ec2::254" or "fe80::a9fe:a9fe";
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            if (address.Equals(IPAddress.IPv6Any)) return true;

            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return true;                 // fc00::/7 unique local
            if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8) return true; // 2001:db8::/32
            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B) return true; // 64:ff9b::/96 NAT64
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                    // 0.0.0.0/8 "this network"
            10 => true,                                   // 10.0.0.0/8
            127 => true,                                  // loopback, already covered but explicit
            169 when b[1] == 254 => true,                 // 169.254.0.0/16 link-local
            172 when b[1] >= 16 && b[1] <= 31 => true,    // 172.16.0.0/12
            192 when b[1] == 168 => true,                 // 192.168.0.0/16
            192 when b[1] == 0 && b[2] == 0 => true,      // 192.0.0.0/24 IETF protocol assignments
            192 when b[1] == 0 && b[2] == 2 => true,      // 192.0.2.0/24 TEST-NET-1
            192 when b[1] == 88 && b[2] == 99 => true,    // 192.88.99.0/24 6to4 relay anycast
            198 when b[1] is 18 or 19 => true,            // 198.18.0.0/15 benchmarking
            198 when b[1] == 51 && b[2] == 100 => true,   // 198.51.100.0/24 TEST-NET-2
            203 when b[1] == 0 && b[2] == 113 => true,    // 203.0.113.0/24 TEST-NET-3
            100 when b[1] >= 64 && b[1] <= 127 => true,   // 100.64.0.0/10 carrier-grade NAT
            >= 224 => true,                               // multicast, reserved, broadcast
            _ => false,
        };
    }

    /// <summary>
    /// Host matching, with one wildcard form: <c>*.example.com</c>.
    ///
    /// The wildcard covers subdomains and not the bare domain, because "*.internal" meaning
    /// "internal" as well is the kind of surprise that turns an allowlist into a suggestion. A
    /// bare <c>example.com</c> entry matches exactly that host.
    /// </summary>
    public static bool HostMatches(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        pattern = pattern.Trim().TrimEnd('.');
        host = host.TrimEnd('.');

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..]; // ".example.com"
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value) => value.Length <= 80 ? value : value[..80] + "…";
}

/// <summary>
/// What one environment is permitted to reach. Built from the environment's settings, so two
/// environments in the same project can have different rules.
/// </summary>
public sealed record UrlPolicy
{
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    public IReadOnlyList<string> DeniedHosts { get; init; } = [];

    /// <summary>Off unless someone deliberately turned it on for this environment.</summary>
    public bool AllowPrivateNetwork { get; init; }

    public int MaxRedirects { get; init; } = 5;

    public long MaxResponseBytes { get; init; } = 4L * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Accept a certificate that does not validate.
    ///
    /// Exists because staging environments genuinely run with self-signed certificates, and a
    /// testing tool that cannot reach them is a tool people work around by disabling something
    /// larger. Per environment, never global.
    /// </summary>
    public bool AllowInvalidCertificate { get; init; }

    /// <summary>The permissive policy used by tests that are not about the guard.</summary>
    public static UrlPolicy Permissive { get; } = new() { AllowPrivateNetwork = true };
}

public sealed record UrlRefusal(UrlRefusalReason Reason, string Message);

public enum UrlRefusalReason
{
    Malformed,
    Scheme,
    Credentials,
    HostDenied,
    HostNotAllowed,
    PrivateAddress,
    TooManyRedirects,
    ResponseTooLarge,
}
