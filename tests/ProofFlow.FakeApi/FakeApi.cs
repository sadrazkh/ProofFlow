using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ProofFlow.FakeApi;

/// <summary>
/// A small, deliberately misbehaving API.
///
/// Every endpoint here exists because some part of ProofFlow needs to be tested against a
/// behaviour that is hard to arrange with a real service: a field that changes on every call, an
/// array that comes back in a different order each time, a job that is not ready until the third
/// poll, an endpoint that fails twice and then works.
///
/// It is a library rather than a standalone service so the integration tests can host it in
/// process, and the demo can run with no internet and no other terminal open.
/// </summary>
public static class FakeApi
{
    public static void MapFakeApi(this IEndpointRouteBuilder app, string prefix = "/fake")
    {
        var group = app.MapGroup(prefix);
        var state = app.ServiceProvider.GetRequiredService<FakeApiState>();

        MapAuthentication(group, state);
        MapCatalog(group, state);
        MapProducts(group, state);
        MapBehaviours(group, state);
    }

    public static IServiceCollection AddFakeApi(this IServiceCollection services)
    {
        services.AddSingleton<FakeApiState>();
        return services;
    }

    // ---- authentication -------------------------------------------------------------------

    private static void MapAuthentication(RouteGroupBuilder group, FakeApiState state)
    {
        // Mints a token that is different every time, which is what makes it a useful subject for
        // "this field is dynamic, ignore it" in a baseline.
        group.MapPost("/auth/login", (LoginRequest request) =>
        {
            if (request.Username != "demo" || request.Password != "demo-password")
                return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);

            var token = $"tok_{Guid.CreateVersion7():N}";
            state.Tokens[token] = DateTimeOffset.UtcNow.AddHours(1);

            return Results.Ok(new
            {
                accessToken = token,
                tokenType = "Bearer",
                expiresIn = 3600,
                issuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                user = new { id = 1, name = "Demo user", roles = new[] { "admin" } },
            });
        });

        group.MapGet("/auth/me", (HttpContext context) =>
            Authorised(context, state, () => Results.Ok(new { id = 1, name = "Demo user" })));
    }

    // ---- catalog --------------------------------------------------------------------------

    private static void MapCatalog(RouteGroupBuilder group, FakeApiState state)
    {
        // Returns its items in a different order on each call. A snapshot comparison that treats
        // arrays as ordered fails here every time, which is exactly the case the "unordered, match
        // by id" rule exists for.
        group.MapGet("/categories", (HttpContext context, bool shuffle = true) =>
            Authorised(context, state, () =>
            {
                var items = state.Categories.ToList();
                if (shuffle) items = [.. items.OrderBy(_ => Random.Shared.Next())];

                return Results.Ok(new
                {
                    items,
                    total = items.Count,
                    requestId = Guid.CreateVersion7().ToString(),
                });
            }));

        group.MapGet("/categories/{id:int}/fields", (HttpContext context, int id) =>
            Authorised(context, state, () =>
            {
                var category = state.Categories.FirstOrDefault(c => c.Id == id);
                if (category is null) return Results.NotFound(new { error = "category_not_found", id });

                return Results.Ok(new
                {
                    categoryId = id,
                    fields = state.FieldsFor(id),
                });
            }));
    }

    // ---- products: a real CRUD lifecycle ----------------------------------------------------

    private static void MapProducts(RouteGroupBuilder group, FakeApiState state)
    {
        group.MapPost("/products", (HttpContext context, ProductRequest request) =>
            Authorised(context, state, () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return Results.Json(new { error = "name_required" }, statusCode: 400);

                var product = new Product(
                    Interlocked.Increment(ref state.NextProductId),
                    request.Name,
                    request.CategoryId,
                    request.Price,
                    DateTimeOffset.UtcNow,
                    request.Attributes ?? new Dictionary<string, string>());

                state.Products[product.Id] = product;
                return Results.Created($"/fake/products/{product.Id}", product);
            }));

        group.MapGet("/products/{id:int}", (HttpContext context, int id) =>
            Authorised(context, state, () =>
                state.Products.TryGetValue(id, out var product)
                    ? Results.Ok(product)
                    : Results.NotFound(new { error = "product_not_found", id })));

        group.MapDelete("/products/{id:int}", (HttpContext context, int id) =>
            Authorised(context, state, () =>
                state.Products.TryRemove(id, out _)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "product_not_found", id })));

        // Paged, so pagination assertions have something real to walk.
        group.MapGet("/products", (HttpContext context, int page = 1, int pageSize = 10) =>
            Authorised(context, state, () =>
            {
                var all = state.Products.Values.OrderBy(p => p.Id).ToList();
                var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                return Results.Ok(new
                {
                    items,
                    page,
                    pageSize,
                    total = all.Count,
                    totalPages = (int)Math.Ceiling(all.Count / (double)pageSize),
                    hasMore = page * pageSize < all.Count,
                });
            }));
    }

    // ---- behaviours worth testing against ---------------------------------------------------

    private static void MapBehaviours(RouteGroupBuilder group, FakeApiState state)
    {
        /// A response that never changes. The control case for snapshot comparison.
        group.MapGet("/stable", () => Results.Ok(new
        {
            id = 1,
            name = "Stable",
            tags = new[] { "a", "b" },
            nested = new { score = 12.5, active = true },
        }));

        // Three fields that differ on every call. What "suggest the dynamic fields" has to find.
        group.MapGet("/volatile", () => Results.Ok(new
        {
            id = 1,
            name = "Volatile",
            requestId = Guid.CreateVersion7().ToString(),
            timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            nonce = Random.Shared.Next(100_000, 999_999),
        }));

        group.MapGet("/slow", async (CancellationToken cancellationToken, int ms = 1500) =>
        {
            await Task.Delay(Math.Clamp(ms, 0, 30_000), cancellationToken);
            return Results.Ok(new { waited = ms });
        });

        group.MapGet("/status/{code:int}", (int code) =>
            Results.Json(new { code, message = $"Deliberate {code}." }, statusCode: code));

        // Fails a set number of times, then succeeds. Retry and flaky detection both need this,
        // and both need it keyed so two tests do not consume each other's attempts.
        group.MapGet("/flaky/{key}", (string key, int failFor = 2) =>
        {
            var attempt = state.Attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

            return attempt <= failFor
                ? Results.Json(new { error = "not_ready", attempt }, statusCode: 503)
                : Results.Ok(new { ok = true, attempt });
        });

        // Not finished until it has been asked enough times. For "poll until".
        group.MapGet("/jobs/{key}", (string key, int readyAfter = 3) =>
        {
            var polls = state.Attempts.AddOrUpdate($"job:{key}", 1, (_, n) => n + 1);

            return Results.Ok(new
            {
                key,
                polls,
                status = polls >= readyAfter ? "completed" : "running",
                result = polls >= readyAfter ? new { value = 42 } : null,
            });
        });

        // Redirects, for the guard: hop count and, with ?to=, an arbitrary destination — which is
        // how the SSRF tests point a redirect at a private address.
        group.MapGet("/redirect/{hops:int}", (int hops, string? to) =>
        {
            if (to is not null) return Results.Redirect(to, permanent: false);
            return hops <= 0
                ? Results.Ok(new { arrived = true })
                : Results.Redirect($"/fake/redirect/{hops - 1}", permanent: false);
        });

        // Larger than any sensible cap, for the response-size limit.
        group.MapGet("/large", (int kilobytes = 8192) =>
            Results.Text(new string('x', Math.Clamp(kilobytes, 1, 65_536) * 1024), "text/plain"));

        group.MapGet("/echo", (HttpContext context) => Results.Ok(new
        {
            method = context.Request.Method,
            path = context.Request.Path.Value,
            query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
            headers = context.Request.Headers
                .Where(h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value.ToString()),
        }));

        // Resets everything the counters above accumulated, so a test run starts from a known state.
        group.MapPost("/reset", () =>
        {
            state.Reset();
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Bearer check, so authentication steps have something that genuinely refuses without a token.
    /// An API that accepts anything cannot demonstrate that a login step worked.
    /// </summary>
    private static IResult Authorised(HttpContext context, FakeApiState state, Func<IResult> handler)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "missing_token" }, statusCode: 401);

        var token = header["Bearer ".Length..].Trim();

        if (!state.Tokens.TryGetValue(token, out var expiry))
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);

        if (expiry < DateTimeOffset.UtcNow)
            return Results.Json(new { error = "expired_token" }, statusCode: 401);

        return handler();
    }
}

public sealed class FakeApiState
{
    public ConcurrentDictionary<string, DateTimeOffset> Tokens { get; } = new();
    public ConcurrentDictionary<int, Product> Products { get; } = new();
    public ConcurrentDictionary<string, int> Attempts { get; } = new();

    public int NextProductId = 5000;

    public IReadOnlyList<Category> Categories { get; } =
    [
        new(11, "Electronics", "electronics"),
        new(12, "Books", "books"),
        new(13, "Garden", "garden"),
    ];

    /// <summary>
    /// Different categories define different fields — which is what makes "fetch the fields, then
    /// build a request from them" a scenario worth being able to express.
    /// </summary>
    public IReadOnlyList<FieldDefinition> FieldsFor(int categoryId) => categoryId switch
    {
        11 => [new("warrantyMonths", "number", true), new("voltage", "string", false)],
        12 => [new("isbn", "string", true), new("pageCount", "number", false)],
        _ => [new("notes", "string", false)],
    };

    public void Reset()
    {
        Tokens.Clear();
        Products.Clear();
        Attempts.Clear();
        Interlocked.Exchange(ref NextProductId, 5000);
    }
}

public sealed record Category(int Id, string Name, string Slug);

public sealed record FieldDefinition(string Name, string Type, bool Required);

public sealed record Product(
    int Id, string Name, int CategoryId, decimal Price,
    DateTimeOffset CreatedAt, IReadOnlyDictionary<string, string> Attributes);

public sealed record LoginRequest(string Username, string Password);

public sealed record ProductRequest(
    string Name, int CategoryId, decimal Price, Dictionary<string, string>? Attributes);
