using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProofFlow.Application.Abstractions;
using ProofFlow.Infrastructure.Mail;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// Somebody who has forgotten their password gets back in, and nobody else does.
///
/// Driven through the real forms — antiforgery tokens, cookies, redirects — because every step of
/// this flow is a place where a shortcut in a test would hide the thing the flow exists to prevent.
/// The mail sender is the only substitution, and only so the link can be read.
/// </summary>
public sealed class PasswordResetTests(ProofFlowApplication app)
    : IClassFixture<ProofFlowApplication>, IAsyncLifetime
{
    private const string Address = "forgetful@example.com";
    private const string FirstPassword = "the first passphrase";
    private const string SecondPassword = "a different passphrase";

    private readonly Recorder _sent = new();

    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        _app = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_sent);
        }));

        // Through the real sign-up form, which is also how we find out it still works.
        var client = Client();
        var token = await TokenAsync(client, "/account/sign-up");

        var response = await PostAsync(client, "/account/sign-up", token,
            ("DisplayName", "Forgetful"),
            ("Email", Address),
            ("Password", FirstPassword),
            ("ConfirmPassword", FirstPassword),
            ("WorkspaceName", "Forgetful's workspace"));

        // A redirect means the account was made. 200 means it already exists, which is the normal
        // case: xUnit runs this before every test in the class, and the account only needs making
        // once.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.OK);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_link_that_arrives_by_email_leads_to_a_new_password_that_works()
    {
        _sent.Messages.Clear();

        var asking = Client();
        var response = await PostAsync(
            asking, "/account/forgot", await TokenAsync(asking, "/account/forgot"),
            ("Email", Address));

        response.Headers.Location!.OriginalString.Should().Be("/account/check-email");

        var message = _sent.Messages.Should().ContainSingle().Subject;
        message.To.Should().Be(Address);

        // The URL is in the plain-text half too. A client that strips HTML must still leave
        // something somebody can copy.
        var link = Regex.Match(message.PlainText, @"https?://\S+").Value;
        link.Should().Contain("/account/reset?u=");

        // Following it in a browser that has never seen this application before, which is what
        // opening a link from an email actually is.
        var opening = Client();
        var path = new Uri(link).PathAndQuery;

        var form = await opening.GetAsync(path);
        form.StatusCode.Should().Be(HttpStatusCode.OK);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);

        var saved = await PostAsync(opening, "/account/reset", await TokenAsync(opening, path),
            ("UserId", query["u"]!),
            ("Token", query["t"]!),
            ("Password", SecondPassword),
            ("ConfirmPassword", SecondPassword));

        saved.Headers.Location!.OriginalString.Should().Be("/account/sign-in");

        // The old one is gone and the new one works. Both halves: a reset that leaves the previous
        // password usable has not reset anything.
        (await SignInAsync(FirstPassword)).Should().Be(HttpStatusCode.OK);
        (await SignInAsync(SecondPassword)).Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task An_address_with_no_account_gets_the_same_answer_and_no_email()
    {
        _sent.Messages.Clear();

        var client = Client();
        var response = await PostAsync(
            client, "/account/forgot", await TokenAsync(client, "/account/forgot"),
            ("Email", "nobody@example.com"));

        // Identical to the answer above. Any difference — a different page, a different sentence —
        // answers the question "does this person have an account here", for every address anybody
        // cares to try.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/account/check-email");

        _sent.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_works_once()
    {
        _sent.Messages.Clear();

        var asking = Client();
        await PostAsync(asking, "/account/forgot", await TokenAsync(asking, "/account/forgot"),
            ("Email", Address));

        var link = Regex.Match(_sent.Messages.Single().PlainText, @"https?://\S+").Value;
        var path = new Uri(link).PathAndQuery;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);

        var opening = Client();

        var first = await PostAsync(opening, "/account/reset", await TokenAsync(opening, path),
            ("UserId", query["u"]!),
            ("Token", query["t"]!),
            ("Password", "one good passphrase"),
            ("ConfirmPassword", "one good passphrase"));

        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var again = await PostAsync(opening, "/account/reset", await TokenAsync(opening, path),
            ("UserId", query["u"]!),
            ("Token", query["t"]!),
            ("Password", "another good passphrase"),
            ("ConfirmPassword", "another good passphrase"));

        // Re-rendered with the refusal rather than redirected, and the refusal is about the link
        // rather than about the password they just typed.
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        // Decoded first: the response encodes non-ASCII as entities, so the Persian sentence is not
        // in the bytes as written.
        WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync())
            .Should().Contain("منقضی");
    }

    [Fact]
    public async Task The_link_is_built_from_configuration_rather_than_from_the_Host_header()
    {
        // Reset poisoning: a forged Host header turns a genuine email from us into a link pointing
        // at somebody else's server. The configured address is what stops it.
        var sent = new Recorder();

        using var configured = app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("App:PublicUrl", "https://proofflow.example.com");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sent);
            });
        });

        var client = configured.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        client.DefaultRequestHeaders.Host = "attacker.example.net";

        await PostAsync(client, "/account/forgot", await TokenAsync(client, "/account/forgot"),
            ("Email", Address));

        var link = Regex.Match(sent.Messages.Single().PlainText, @"https?://\S+").Value;

        link.Should().StartWith("https://proofflow.example.com/account/reset");
        link.Should().NotContain("attacker.example.net");
    }

    [Fact]
    public void A_sender_with_no_host_configured_says_so_rather_than_pretending()
    {
        var sender = new SmtpEmailSender(
            Options.Create(new SmtpOptions()), NullLogger<SmtpEmailSender>.Instance);

        sender.CanSend.Should().BeFalse();
    }

    [Fact]
    public void A_sender_with_a_host_but_no_address_to_send_from_is_not_ready_either()
    {
        var sender = new SmtpEmailSender(
            Options.Create(new SmtpOptions { Host = "smtp.example.com", UserName = "apikey" }),
            NullLogger<SmtpEmailSender>.Instance);

        // "apikey" is a username, not an address. Sending from it would be rejected by the relay
        // after the fact, which is a worse place to find out.
        sender.CanSend.Should().BeFalse();
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private HttpClient Client() =>
        _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpStatusCode> SignInAsync(string password)
    {
        var client = Client();

        var response = await PostAsync(client, "/account/sign-in", await TokenAsync(client, "/account/sign-in"),
            ("Email", Address),
            ("Password", password),
            ("RememberMe", "true"));

        // A redirect means it worked; 200 means the form came back with an error on it.
        return response.StatusCode;
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);

        var match = Regex.Match(
            html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");

        match.Success.Should().BeTrue($"{path} should carry an antiforgery token");

        return match.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, string token, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
        };

        form.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

        return client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    /// <summary>
    /// A mail server that keeps everything instead of sending it.
    ///
    /// The only substitution in these tests, and it exists so the link can be read — not so the flow
    /// can be skipped.
    /// </summary>
    private sealed class Recorder : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public bool CanSend => true;

        public Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult<string?>(null);
        }
    }
}
