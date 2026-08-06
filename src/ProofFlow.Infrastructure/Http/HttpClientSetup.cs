using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Http;

/// <summary>
/// Where the SSRF guard actually holds.
///
/// Everything above this is advisory. A hostname check can be defeated by a name that resolves to
/// a public address when the guard looks and a private one when the socket connects — DNS
/// rebinding, and it needs no privileged access to arrange, only a domain and a short TTL.
///
/// So the connect callback resolves the name itself, refuses every address it is not allowed to
/// reach, and then connects to one of the addresses it just approved. There is no second lookup
/// between the check and the connection, which is the only arrangement that closes the window.
/// </summary>
public static class HttpClientSetup
{
    /// <summary>
    /// Carries the environment's policy down to the connect callback.
    ///
    /// Through the request rather than through a field, because the handler is pooled and shared:
    /// a field would let one environment's "reach private networks" apply to the next request from
    /// a different environment.
    /// </summary>
    public static readonly HttpRequestOptionsKey<UrlPolicy> PolicyKey = new("proofflow.url-policy");

    public static IServiceCollection AddProofFlowHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient(PolicyClientNames.Strict)
            .ConfigurePrimaryHttpMessageHandler(() => Handler(allowInvalidCertificate: false));

        // A separate named client rather than a per-request switch: certificate validation belongs
        // to the handler, and handlers are pooled. Toggling it per request means a new handler per
        // request, which exhausts sockets under any real load.
        services.AddHttpClient(PolicyClientNames.AllowInvalidCertificate)
            .ConfigurePrimaryHttpMessageHandler(() => Handler(allowInvalidCertificate: true));

        return services;
    }

    private static SocketsHttpHandler Handler(bool allowInvalidCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            // Off, always. Redirects are followed by GuardedHttpExecutor so the guard runs on every
            // hop; the handler's own redirect would skip all of it.
            AllowAutoRedirect = false,

            // Cookies are per-run state the engine manages, not per-process state a handler
            // accumulates. A shared container would leak one project's session into another's run.
            UseCookies = false,

            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectAsync,
        };

        if (allowInvalidCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var policy = context.InitialRequestMessage.Options.TryGetValue(PolicyKey, out var carried)
            ? carried
            // No policy attached means a caller outside the engine. Refuse private networking
            // rather than assume it — the safe reading is the one that fails visibly.
            : new UrlPolicy();

        var guard = new UrlGuard(policy);
        var endpoint = context.DnsEndPoint;

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var allowed = addresses.Where(guard.IsAddressAllowed).ToArray();

        if (allowed.Length == 0)
        {
            // Named in the message: a host that resolves only to private space is nearly always a
            // misconfiguration, and telling the operator which address it was saves the round trip.
            throw new HttpRequestException(
                $"{endpoint.Host} resolves to {string.Join(", ", addresses.Select(a => a.ToString()))}, " +
                "which this environment is not allowed to reach.",
                new SocketException((int)SocketError.AccessDenied));
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // Connects to the addresses just approved, never to the host name again. Re-resolving
            // here would reopen the exact window this method exists to close.
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
