using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Projects;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// One webhook delivery: payload, signature, guarded send.
///
/// Shared by the delivery worker and the settings page's «send a test» button, because a test
/// button that goes through different code than the real thing tests the button.
/// </summary>
public static class WebhookSender
{
    public sealed record Delivery(bool Ok, string Detail);

    public static async Task<Delivery> SendAsync(
        IHttpClientFactory http, ISecretCipher cipher, Project project, string payload,
        CancellationToken cancellation)
    {
        if (project.WebhookUrl is not { Length: > 0 } hook)
        {
            return new Delivery(false, "no webhook url");
        }

        var headers = new List<KeyValueEntry>();

        if (project.WebhookSecretCipher is { Length: > 0 })
        {
            // The signature is why the secret is sealed rather than hashed: signing needs the
            // value back on every delivery.
            var secret = cipher.Open(new SealedSecret(
                project.WebhookSecretCipher, project.WebhookSecretNonce!,
                project.WebhookSecretTag!, project.WebhookSecretKeyVersion));

            var signature = Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

            headers.Add(new KeyValueEntry("X-ProofFlow-Signature", $"sha256={signature}"));
        }

        // The guarded executor, because a webhook URL is user input: same SSRF rules, same
        // timeouts, same refusal to follow a redirect into somebody's metadata service.
        var response = await new GuardedHttpExecutor(http, NullLogger<GuardedHttpExecutor>.Instance)
            .SendAsync(
                new HttpRequestDefinition
                {
                    Method = "POST",
                    Url = hook,
                    Headers = headers,
                    Body = new RequestBody { Kind = BodyKind.Json, Content = payload },
                },
                new UrlPolicy
                {
                    AllowPrivateNetwork = project.WebhookAllowPrivate,
                    Timeout = TimeSpan.FromSeconds(10),
                },
                cancellation);

        return response.Succeeded && response.StatusCode is >= 200 and < 300
            ? new Delivery(true, $"HTTP {response.StatusCode}")
            : new Delivery(false, response.Succeeded
                ? $"HTTP {response.StatusCode}"
                : response.Failure!.Message);
    }

    public static string Payload(
        string kind, string projectName, string? target, string message, string? link,
        DateTimeOffset occurredAt) =>
        JsonSerializer.Serialize(new { kind, project = projectName, target, message, link, occurredAt });
}
