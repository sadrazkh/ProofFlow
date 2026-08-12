using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;
using ProofFlow.TestEngine.Redaction;

namespace ProofFlow.TestEngine.Http;

/// <summary>
/// The real network, behind the guard.
///
/// Redirects are followed by hand rather than by <c>HttpClient</c>. That is the whole point: the
/// handler's automatic redirect does not re-run any policy, so one 302 to <c>169.254.169.254</c>
/// walks straight past every check made on the original URL. Following them here means the guard
/// runs on each hop, and the chain ends up in the run report where a reader can see where it went.
/// </summary>
public sealed class GuardedHttpExecutor(
    IHttpClientFactory clientFactory,
    ILogger<GuardedHttpExecutor> logger) : IHttpExecutor
{
    /// <summary>
    /// Read in chunks so the size cap applies to what arrives, not to what a Content-Length header
    /// claims. A hostile or broken server can send far more than it announced, and
    /// <c>ReadAsStringAsync</c> would happily buffer all of it.
    /// </summary>
    private const int ReadBufferBytes = 64 * 1024;

    public async Task<HttpExchangeResult> SendAsync(
        HttpRequestDefinition request, UrlPolicy policy, CancellationToken cancellationToken = default)
    {
        var guard = new UrlGuard(policy);
        var url = BuildUrl(request);

        if (guard.Inspect(url) is { } refusal)
            return Blocked(request, url, refusal);

        var attempts = 0;
        var maxAttempts = Math.Max(1, request.Retry.MaxAttempts);
        HttpExchangeResult? last = null;

        while (attempts < maxAttempts)
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();

            last = await SendOnceAsync(request, policy, guard, url, attempts, cancellationToken);

            if (!ShouldRetry(last, request.Retry) || attempts >= maxAttempts) break;

            var delay = request.Retry.ExponentialBackoff
                ? request.Retry.DelayMilliseconds * (int)Math.Pow(2, attempts - 1)
                : request.Retry.DelayMilliseconds;

            await Task.Delay(Math.Min(delay, 30_000), cancellationToken);
        }

        // Attempts is carried out, always. A retry that succeeds after two failures is not the same
        // event as one that succeeded immediately, and a report that hides the difference turns a
        // flaky endpoint into a green tick.
        return last! with { Attempts = attempts };
    }

    private async Task<HttpExchangeResult> SendOnceAsync(
        HttpRequestDefinition request, UrlPolicy policy, UrlGuard guard,
        string url, int attempt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var chain = new List<string>();
        var current = url;

        var client = clientFactory.CreateClient(
            policy.AllowInvalidCertificateClientName());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.Timeout);

        try
        {
            for (var hop = 0; ; hop++)
            {
                if (hop > policy.MaxRedirects)
                {
                    return Failed(request, url, stopwatch.Elapsed, chain, attempt, new HttpFailure(
                        HttpFailureKind.TooManyRedirects,
                        $"The address redirected more than {policy.MaxRedirects} times. " +
                        "That is usually a redirect loop rather than a slow one.",
                        string.Join(" → ", chain)));
                }

                if (guard.Inspect(current) is { } refusal)
                {
                    // Only reachable on a redirect: the first URL was inspected before the loop.
                    return Failed(request, url, stopwatch.Elapsed, chain, attempt, new HttpFailure(
                        HttpFailureKind.BlockedByPolicy,
                        $"A redirect led somewhere ProofFlow will not follow. {refusal.Message}",
                        string.Join(" → ", chain.Append(current))));
                }

                using var message = BuildMessage(request, current, hop);
                // Carried on the message so the connect callback — which is where the guard
                // actually holds — knows what this environment is allowed to reach.
                message.Options.Set(HttpClientSetup.PolicyKey, policy);

                using var response = await client.SendAsync(
                    message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri
                        ? location.ToString()
                        : new Uri(new Uri(current), location).ToString();

                    chain.Add(next);
                    current = next;
                    continue;
                }

                var (body, bytes, truncated) = await ReadBoundedAsync(response, policy.MaxResponseBytes, timeout.Token);
                stopwatch.Stop();

                if (truncated)
                {
                    return Failed(request, current, stopwatch.Elapsed, chain, attempt, new HttpFailure(
                        HttpFailureKind.ResponseTooLarge,
                        $"The response was larger than this environment's limit of " +
                        $"{policy.MaxResponseBytes / 1024} KB, so it was not read. Raise the limit in " +
                        "the environment settings if that size is expected."));
                }

                return new HttpExchangeResult
                {
                    ResolvedUrl = current,
                    Method = request.Method,
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    ResponseHeaders = ReadHeaders(response),
                    Body = body,
                    BodyBytes = bytes,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Duration = stopwatch.Elapsed,
                    RedirectChain = chain,
                    Attempts = attempt,
                    SentHeaders = Redactor.RedactHeaders(SentHeaders(request)),
                    SentBody = request.Body?.Content is { } sent ? Redactor.Redact(sent) : null,
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failed(request, current, stopwatch.Elapsed, chain, attempt,
                new HttpFailure(HttpFailureKind.Cancelled, "The run was cancelled."));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failed(request, current, stopwatch.Elapsed, chain, attempt, new HttpFailure(
                HttpFailureKind.Timeout,
                $"No response within {policy.Timeout.TotalSeconds:0} seconds."));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();

            // Logged, not just returned. The sentence a person reads is deliberately short, and the
            // exception underneath it is the only thing that says which of a dozen transport
            // failures this was — an operator looking at a run that will not connect needs it.
            logger.LogInformation(ex, "The request to {Url} did not complete.", current);

            return Failed(request, current, stopwatch.Elapsed, chain, attempt, Diagnose(ex));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Unexpected failure requesting {Url}.", current);
            return Failed(request, current, stopwatch.Elapsed, chain, attempt, new HttpFailure(
                HttpFailureKind.Unknown, "The request could not be completed.", ex.Message));
        }
    }

    /// <summary>
    /// Turns a transport exception into a sentence with an action in it.
    ///
    /// "The SSL connection could not be established" is true and useless. Somebody testing a
    /// staging box with a self-signed certificate needs to be told that a switch exists.
    /// </summary>
    private static HttpFailure Diagnose(HttpRequestException ex)
    {
        var inner = ex.InnerException;

        if (inner is AuthenticationException || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            return new HttpFailure(HttpFailureKind.Certificate,
                "The server's TLS certificate was not accepted. If this environment uses a " +
                "self-signed certificate, allow it in the environment settings.", inner?.Message);

        if (inner is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => new HttpFailure(
                    HttpFailureKind.DnsFailure,
                    "That host name does not resolve. Check the base URL for a typo.", socket.Message),

                SocketError.ConnectionRefused => new HttpFailure(
                    HttpFailureKind.Refused,
                    "The server refused the connection. It may not be listening on that port.", socket.Message),

                SocketError.TimedOut => new HttpFailure(
                    HttpFailureKind.Timeout, "The connection timed out.", socket.Message),

                // The guard rejects at connect time by aborting the socket, which surfaces here.
                SocketError.AccessDenied or SocketError.NetworkUnreachable => new HttpFailure(
                    HttpFailureKind.BlockedByPolicy,
                    "That address is on a network this environment may not reach.", socket.Message),

                _ => new HttpFailure(HttpFailureKind.Refused, "The connection failed.", socket.Message),
            };
        }

        return new HttpFailure(HttpFailureKind.Refused, "The request did not reach the server.", ex.Message);
    }

    private static async Task<(string Body, long Bytes, bool Truncated)> ReadBoundedAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[ReadBufferBytes];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;

            total += read;
            if (total > maxBytes) return (string.Empty, total, true);

            buffer.Write(chunk, 0, read);
        }

        var encoding = ResolveEncoding(response.Content.Headers.ContentType);
        return (encoding.GetString(buffer.ToArray()), total, false);
    }

    /// <summary>
    /// The charset the server declared, falling back to UTF-8.
    ///
    /// Not <c>Encoding.Default</c>: that is the operating system's code page, so the same response
    /// would decode differently on a Windows runner and a Linux one, and a snapshot captured on one
    /// would never match a run on the other.
    /// </summary>
    private static Encoding ResolveEncoding(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.CharSet is not { Length: > 0 } charset) return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static HttpRequestMessage BuildMessage(HttpRequestDefinition request, string url, int hop)
    {
        // A redirect turns any method into a GET with no body, which is what a browser does and
        // what every server expects. Replaying a POST body to a new address is how a redirect
        // becomes a duplicate order.
        var method = hop == 0 ? new HttpMethod(request.Method) : HttpMethod.Get;
        var message = new HttpRequestMessage(method, url);

        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
                message.Content ??= new StringContent(string.Empty);
        }

        if (hop == 0 && request.Body is { Kind: not BodyKind.None } body)
        {
            message.Content = BuildContent(body);

            foreach (var header in request.Headers.Where(h => h.Enabled))
                message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return message;
    }

    private static HttpContent BuildContent(RequestBody body)
    {
        if (body.Kind == BodyKind.FormUrlEncoded)
        {
            return new FormUrlEncodedContent(
                body.Form.Where(f => f.Enabled).Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        }

        var mediaType = body.ContentType ?? body.Kind switch
        {
            BodyKind.Json or BodyKind.GraphQl => "application/json",
            BodyKind.Xml => "application/xml",
            _ => "text/plain",
        };

        return new StringContent(body.Content ?? string.Empty, Encoding.UTF8, mediaType.Split(';')[0]);
    }

    private static string BuildUrl(HttpRequestDefinition request)
    {
        var enabled = request.Query.Where(q => q.Enabled).ToList();
        if (enabled.Count == 0) return request.Url;

        var separator = request.Url.Contains('?') ? '&' : '?';
        var query = string.Join('&', enabled.Select(q =>
            $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));

        return request.Url + separator + query;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool ShouldRetry(HttpExchangeResult result, RetryPolicy retry)
    {
        if (result.Failure is { Kind: HttpFailureKind.Cancelled or HttpFailureKind.BlockedByPolicy })
            return false;

        if (result.Failure is not null) return true;

        return retry.RetryOnStatus.Contains(result.StatusCode);
    }

    private static IReadOnlyList<KeyValueEntry> ReadHeaders(HttpResponseMessage response) =>
    [
        .. response.Headers.Select(h => new KeyValueEntry(h.Key, string.Join(", ", h.Value))),
        .. response.Content.Headers.Select(h => new KeyValueEntry(h.Key, string.Join(", ", h.Value))),
    ];

    private static IReadOnlyList<KeyValueEntry> SentHeaders(HttpRequestDefinition request) =>
        [.. request.Headers.Where(h => h.Enabled)];

    private static HttpExchangeResult Blocked(HttpRequestDefinition request, string url, UrlRefusal refusal) =>
        new()
        {
            ResolvedUrl = url,
            Method = request.Method,
            Failure = new HttpFailure(
                refusal.Reason == UrlRefusalReason.PrivateAddress
                    ? HttpFailureKind.BlockedByPolicy
                    : HttpFailureKind.BlockedByPolicy,
                refusal.Message),
        };

    private static HttpExchangeResult Failed(
        HttpRequestDefinition request, string url, TimeSpan elapsed,
        IReadOnlyList<string> chain, int attempt, HttpFailure failure) =>
        new()
        {
            ResolvedUrl = url,
            Method = request.Method,
            Duration = elapsed,
            RedirectChain = chain,
            Attempts = attempt,
            SentHeaders = Redactor.RedactHeaders(SentHeaders(request)),
            Failure = failure,
        };
}

public static class PolicyClientNames
{
    /// <summary>
    /// Two named clients, because certificate validation is a handler concern and a handler is
    /// pooled. Switching validation per request would mean a new handler per request, which leaks
    /// sockets under any real load.
    /// </summary>
    public const string Strict = "proofflow.http.strict";
    public const string AllowInvalidCertificate = "proofflow.http.lax";

    public static string AllowInvalidCertificateClientName(this UrlPolicy policy) =>
        policy.AllowInvalidCertificate ? AllowInvalidCertificate : Strict;
}
