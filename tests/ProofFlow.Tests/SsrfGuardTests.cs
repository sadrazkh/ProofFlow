using System.Net;
using FluentAssertions;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// The guard that stops ProofFlow being turned into a proxy into its own network.
///
/// This is the test file to read first when changing anything about outbound requests. The
/// application's whole purpose is to fetch a URL a user supplied, which means the difference
/// between a testing tool and a credential-exfiltration tool is entirely in here.
/// </summary>
public class SsrfGuardTests
{
    private static readonly UrlGuard Default = new(new UrlPolicy());
    private static readonly UrlGuard PrivateAllowed = new(new UrlPolicy { AllowPrivateNetwork = true });

    [Theory]
    [InlineData("169.254.169.254")]   // AWS, Azure, GCP, DigitalOcean
    [InlineData("169.254.170.2")]     // ECS task role credentials
    [InlineData("100.100.100.200")]   // Alibaba Cloud
    public void Metadata_addresses_are_refused_even_when_private_networking_is_allowed(string address)
    {
        // The one rule with no escape hatch. Someone testing an API on their own LAN has a real
        // reason to reach 10.0.0.0/8; nobody has a reason to make the server read its own instance
        // credentials, and the person who wants to is not the operator.
        var ip = IPAddress.Parse(address);

        Default.IsAddressAllowed(ip).Should().BeFalse();
        PrivateAllowed.IsAddressAllowed(ip).Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]        // carrier-grade NAT
    [InlineData("192.0.0.1")]         // IETF protocol assignments
    [InlineData("198.18.0.1")]        // benchmarking range
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("255.255.255.255")]
    public void Private_and_reserved_addresses_are_refused_by_default(string address) =>
        Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]           // link-local
    [InlineData("fc00::1")]           // unique local
    [InlineData("fd12:3456::1")]      // unique local
    [InlineData("ff02::1")]           // multicast
    [InlineData("2001:db8::1")]       // documentation range
    public void IPv6_private_ranges_are_refused(string address) =>
        Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void An_IPv4_address_written_in_IPv6_form_is_still_that_address(string address)
    {
        // ::ffff:127.0.0.1 is 127.0.0.1 wearing a hat. Without unwrapping, every rule above is
        // bypassed by rewriting the address — the single cheapest way past a guard like this.
        Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeFalse();
        PrivateAllowed.IsAddressAllowed(IPAddress.Parse("::ffff:169.254.169.254")).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void Public_addresses_are_allowed(string address) =>
        Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeTrue();

    [Fact]
    public void Private_addresses_are_allowed_when_the_environment_says_so()
    {
        // Off by default, on deliberately. A tool that cannot reach a developer's own machine is
        // a tool they will run around rather than with.
        PrivateAllowed.IsAddressAllowed(IPAddress.Parse("192.168.1.10")).Should().BeTrue();
        PrivateAllowed.IsAddressAllowed(IPAddress.Parse("127.0.0.1")).Should().BeTrue();
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("ftp://example.com/")]
    [InlineData("dict://localhost:11211/")]
    [InlineData("jar:http://example.com!/")]
    public void Only_http_and_https_are_accepted(string url) =>
        Default.Inspect(url)!.Reason.Should().BeOneOf(UrlRefusalReason.Scheme, UrlRefusalReason.Malformed);

    [Fact]
    public void Credentials_in_the_address_are_refused()
    {
        // Not a network concern — a disclosure one. The URL is written into the run report, the
        // exported file and the audit log, where the password stops being a password.
        var refusal = Default.Inspect("https://user:hunter2@example.com/api");

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(UrlRefusalReason.Credentials);
    }

    [Fact]
    public void A_literal_private_address_is_caught_before_the_request()
    {
        var refusal = Default.Inspect("http://127.0.0.1:8080/admin");

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(UrlRefusalReason.PrivateAddress);
        // The message has to say what to do, not just what happened.
        refusal.Message.Should().Contain("private");
    }

    [Fact]
    public void The_metadata_address_is_explained_differently_from_an_ordinary_private_one()
    {
        Default.Explain(IPAddress.Parse("169.254.169.254")).Should().Contain("credentials");
        Default.Explain(IPAddress.Parse("192.168.0.1")).Should().Contain("settings");
    }

    [Fact]
    public void An_allowed_list_excludes_everything_it_does_not_name()
    {
        var guard = new UrlGuard(new UrlPolicy { AllowedHosts = ["api.example.com", "*.trusted.dev"] });

        guard.Inspect("https://api.example.com/orders").Should().BeNull();
        guard.Inspect("https://staging.trusted.dev/orders").Should().BeNull();
        guard.Inspect("https://evil.com/orders")!.Reason.Should().Be(UrlRefusalReason.HostNotAllowed);
    }

    [Fact]
    public void A_wildcard_covers_subdomains_and_not_the_bare_domain()
    {
        // "*.internal" quietly meaning "internal" too is the kind of surprise that turns an
        // allowlist into a suggestion.
        UrlGuard.HostMatches("api.example.com", "*.example.com").Should().BeTrue();
        UrlGuard.HostMatches("deep.api.example.com", "*.example.com").Should().BeTrue();
        UrlGuard.HostMatches("example.com", "*.example.com").Should().BeFalse();
        UrlGuard.HostMatches("notexample.com", "*.example.com").Should().BeFalse();
    }

    [Fact]
    public void A_denied_host_beats_an_allowed_one()
    {
        var guard = new UrlGuard(new UrlPolicy
        {
            AllowedHosts = ["*.example.com"],
            DeniedHosts = ["admin.example.com"],
        });

        guard.Inspect("https://api.example.com/x").Should().BeNull();
        guard.Inspect("https://admin.example.com/x")!.Reason.Should().Be(UrlRefusalReason.HostDenied);
    }

    [Fact]
    public void Host_matching_ignores_case_and_a_trailing_dot()
    {
        // "example.com." is the fully qualified form and resolves identically, so an allowlist
        // that misses it is an allowlist with a typo-shaped hole in it.
        UrlGuard.HostMatches("API.Example.COM", "api.example.com").Should().BeTrue();
        UrlGuard.HostMatches("api.example.com.", "api.example.com").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void Anything_that_is_not_an_absolute_url_is_refused(string url) =>
        Default.Inspect(url)!.Reason.Should().Be(UrlRefusalReason.Malformed);
}
