using System.Net;
using FluentAssertions;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The application starts, migrates, and answers. Cheap to run and the first thing to break when
/// dependency registration or a migration goes wrong.
/// </summary>
public class ShellSmokeTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    [Fact]
    public async Task Health_answers_without_a_session()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ok");
    }

    [Fact]
    public async Task The_dashboard_sends_an_anonymous_visitor_to_sign_in()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/sign-in");
    }

    [Fact]
    public async Task The_sign_in_page_renders_in_Persian_by_default()
    {
        var client = app.CreateClient();

        var html = await client.GetStringAsync("/account/sign-in");

        html.Should().Contain("dir=\"rtl\"");
        html.Should().Contain("lang=\"fa\"");
        html.Should().Contain("ورود");
    }

    [Fact]
    public async Task The_sign_in_page_renders_in_English_when_asked()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "en");

        var html = await client.GetStringAsync("/account/sign-in");

        html.Should().Contain("dir=\"ltr\"");
        html.Should().Contain("Sign in");
    }

    [Fact]
    public async Task An_unknown_path_renders_the_themed_404_and_keeps_its_status()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/no-such-page");
        var html = await response.Content.ReadAsStringAsync();

        // Both halves matter. A themed page that answers 200 tells every crawler and every
        // monitoring check that a missing page is fine.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        html.Should().Contain("404");
    }

    [Fact]
    public async Task Security_headers_are_on_every_response()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/account/sign-in");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task The_language_switch_sets_a_cookie_and_returns_to_the_page()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/settings/language?culture=en&returnUrl=%2Faccount%2Fsign-in");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/account/sign-in");
        response.Headers.GetValues("Set-Cookie").Should().Contain(value => value.Contains(".AspNetCore.Culture"));
    }

    [Fact]
    public async Task The_language_switch_refuses_a_culture_the_app_does_not_have()
    {
        var client = app.CreateClient();

        var response = await client.GetAsync("/settings/language?culture=de");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_language_switch_will_not_bounce_to_another_site()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // An open redirect on a link that legitimately comes from us is a phishing tool.
        var response = await client.GetAsync("/settings/language?culture=en&returnUrl=https%3A%2F%2Fexample.com");

        response.Headers.Location!.OriginalString.Should().Be("/");
    }
}
