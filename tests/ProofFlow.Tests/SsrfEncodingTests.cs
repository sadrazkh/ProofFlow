using FluentAssertions;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Tests;

/// <summary>
/// The same forbidden address, written every way somebody writes it when they are trying.
///
/// This is the half of SSRF that is about notation rather than about networks. 127.0.0.1 is also
/// 2130706433, and 0x7f.0.0.1, and 0177.0.0.1, and 127.1, and [::ffff:127.0.0.1], and every one of
/// those is a URL a library will happily dial. A guard that inspects the text of a hostname and
/// stops there is a guard that has been walked past.
///
/// These are here as a gate. Not because a bypass is likely today — the checks below mostly pass
/// because .NET normalises the notation before the guard ever sees it — but because that is a
/// property of a dependency rather than a decision this project made, and it should fail loudly on
/// the day it changes.
/// </summary>
public class SsrfEncodingTests
{
    private static readonly UrlGuard Default = new(new UrlPolicy());
    private static readonly UrlGuard Permissive = new(UrlPolicy.Permissive);

    [Theory]
    [InlineData("http://2130706433/", "decimal")]
    [InlineData("http://0x7f000001/", "hexadecimal")]
    [InlineData("http://017700000001/", "octal")]
    [InlineData("http://127.1/", "short form")]
    [InlineData("http://127.0.1/", "three-part form")]
    [InlineData("http://0/", "zero")]
    public void Loopback_written_in_another_notation_is_still_loopback(string url, string notation)
    {
        Default.Inspect(url).Should().NotBeNull($"{url} is loopback in {notation}");
    }

    [Theory]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[::ffff:a9fe:a9fe]/")]
    public void The_metadata_address_wearing_an_IPv6_hat_is_refused(string url)
    {
        // Refused under the permissive policy too — the metadata rule is the one that does not bend
        // for an environment that has opted into private networking.
        Permissive.Inspect(url).Should().NotBeNull();
        Permissive.Inspect(url)!.Reason.Should().Be(UrlRefusalReason.PrivateAddress);
    }

    [Theory]
    [InlineData("http://[::1]/")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/")]
    [InlineData("http://[fd00::1]/")]
    public void A_bracketed_IPv6_literal_is_judged_like_any_other_address(string url)
    {
        Default.Inspect(url).Should().NotBeNull();
    }

    [Theory]
    [InlineData("http://2852039166/")]      // 169.254.169.254 in decimal
    [InlineData("http://0xa9fea9fe/")]      // and in hexadecimal
    public void The_metadata_address_in_another_notation_is_refused_under_any_policy(string url)
    {
        Default.Inspect(url).Should().NotBeNull();
        Permissive.Inspect(url).Should().NotBeNull();
    }

    [Fact]
    public void A_public_address_in_decimal_is_still_allowed()
    {
        // The rule is about which network, not about which notation. Refusing everything unusual
        // would be a guard that also blocks legitimate requests, which is how guards get switched
        // off.
        Default.Inspect("http://134744072/").Should().BeNull("that is 8.8.8.8");
    }

    [Theory]
    [InlineData("http://127.0.0.1@example.com/")]
    [InlineData("http://example.com@127.0.0.1/")]
    public void An_address_with_credentials_in_it_is_refused_before_anybody_argues_about_which_host_it_names(
        string url)
    {
        // The second form is the trick — everything before the @ is a username, so this dials
        // 127.0.0.1 while reading like example.com. Refusing credentials outright settles it.
        Default.Inspect(url)!.Reason.Should().Be(UrlRefusalReason.Credentials);
    }

    [Theory]
    [InlineData("HTTP://127.0.0.1/")]
    [InlineData("HtTpS://[::1]/")]
    public void The_scheme_is_read_case_insensitively(string url)
    {
        // Uppercase is still http, so this must be refused for being loopback rather than waved
        // through for having an unrecognised scheme — or accidentally refused for the wrong reason,
        // which would leave the real check untested.
        Default.Inspect(url)!.Reason.Should().Be(UrlRefusalReason.PrivateAddress);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11211/")]
    [InlineData("http://10.0.0.1:6379/")]
    [InlineData("http://[::1]:9200/")]
    public void A_port_does_not_change_the_answer(string url)
    {
        // The interesting ports for an attacker are the unauthenticated ones on the loopback
        // interface, so this is the case worth stating out loud.
        Default.Inspect(url).Should().NotBeNull();
    }

    [Fact]
    public void An_allowed_list_does_not_override_the_address_rules()
    {
        // Naming a host does not make its address public. Somebody who allows «internal.example.com»
        // and finds it resolves to 10.0.0.5 still has to opt into private networking on purpose.
        var guard = new UrlGuard(new UrlPolicy { AllowedHosts = ["10.0.0.5"] });

        guard.Inspect("http://10.0.0.5/").Should().NotBeNull();
        guard.Inspect("http://10.0.0.5/")!.Reason.Should().Be(UrlRefusalReason.PrivateAddress);
    }

    [Fact]
    public void The_denied_list_is_checked_before_anything_else_about_the_host()
    {
        var guard = new UrlGuard(new UrlPolicy
        {
            AllowPrivateNetwork = true,
            DeniedHosts = ["*.internal"],
        });

        guard.Inspect("http://billing.internal/")!.Reason.Should().Be(UrlRefusalReason.HostDenied);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("100.100.100.200")]
    [InlineData("::ffff:169.254.169.254")]
    public void Every_metadata_address_survives_the_permissive_policy(string address)
    {
        // The one rule with no override. Somebody testing an API on their own LAN has a reason to
        // reach 10.0.0.0/8; nobody has a reason to make this server read its own instance
        // credentials, and the one person who wants to is not the operator.
        Permissive.IsAddressAllowed(System.Net.IPAddress.Parse(address)).Should().BeFalse();
    }
}
