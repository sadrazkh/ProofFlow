namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// What to do with a credential that arrives inside a pasted file.
///
/// Every one of the three importers hits the same problem. Somebody copies a working cURL command
/// out of their terminal, or exports a Postman collection with a live bearer token in it, and hands
/// it over. That token is real. Writing it into a scenario would put it in the database as plain
/// text, in every export, in the diff viewer, and in a screenshot the first time anybody reviews
/// the page.
///
/// So the name travels and the value does not. The header is kept, pointing at
/// <c>{{secrets.name}}</c>, the reader is told which secret to create, and the value they pasted is
/// dropped on the floor without ever being stored. It costs one step and it is the difference
/// between a product that handles secrets and one that has a secrets page.
/// </summary>
internal static class Credentials
{
    /// <summary>
    /// Headers whose value is a credential rather than a setting.
    ///
    /// Matched by whole name, not by substring, so <c>x-request-id</c> is not mistaken for one.
    /// </summary>
    private static readonly HashSet<string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "x-api-key",
        "api-key",
        "apikey",
        "x-auth-token",
        "x-access-token",
    };

    /// <summary>
    /// And the ones nobody standardised. A substring match, because this half is a guess — which is
    /// why it is separate from the list above and why guessing wrong is cheap: the reader is told
    /// what happened and can put the value back as an ordinary header if it was not a secret.
    /// </summary>
    private static readonly string[] Suspicious = ["token", "secret", "password", "credential"];

    public static bool IsCredential(string headerName) =>
        Named.Contains(headerName.Trim())
        || Suspicious.Any(word => headerName.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name of the secret a header should point at.
    ///
    /// <c>Authorization</c> becomes <c>authorization</c> and <c>X-Api-Key</c> becomes
    /// <c>xApiKey</c>, because that is what every other name in a variable reference looks like.
    ///
    /// Only the first letter of each part is touched. Lower-casing the rest would turn
    /// <c>authToken</c> — a name somebody already wrote in this shape — into <c>authtoken</c>, and
    /// then the reference in the scenario would not match the secret they were told to create.
    /// </summary>
    public static string SecretName(string headerName)
    {
        var parts = headerName
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count == 0) return "secret";

        return string.Concat(
            char.ToLowerInvariant(parts[0][0]) + parts[0][1..],
            string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..])));
    }

    public static string Reference(string headerName) => $"{{{{secrets.{SecretName(headerName)}}}}}";
}
