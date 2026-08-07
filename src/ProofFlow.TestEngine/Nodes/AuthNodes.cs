namespace ProofFlow.TestEngine.Nodes;

/// <summary>
/// Getting a token, and keeping it.
///
/// Every credential here is a <see cref="PropertyKind.SecretRef"/> — a name, never a value. The
/// browser is told that <c>apiToken</c> exists; the value is decrypted on the server at the moment
/// of the request and redacted out of everything that comes back. A password box on this canvas
/// would put a real credential into a saved graph, an export, and a screenshot.
/// </summary>
public static class AuthNodes
{
    /// <summary>Where a minted token comes out. Typed <c>Secret</c>, so it can only be plugged in
    /// where a credential is wanted.</summary>
    private static PortSpec TokenOut => Port.Data("token", DataType.Secret);

    public static IReadOnlyList<NodeSpec> All =>
    [
        new()
        {
            Key = "auth.basic",
            Group = NodeGroup.Auth,
            Icon = "key-round",
            Outputs = [Port.Out, TokenOut],
            Properties =
            [
                new() { Name = "username", LabelKey = "nodeprop.username", Kind = PropertyKind.Text, Required = true },
                new() { Name = "password", LabelKey = "nodeprop.password", Kind = PropertyKind.SecretRef, Required = true },
            ],
        },
        new()
        {
            Key = "auth.bearer",
            Group = NodeGroup.Auth,
            Icon = "key",
            Outputs = [Port.Out, TokenOut],
            Properties =
            [
                new() { Name = "token", LabelKey = "nodeprop.token", Kind = PropertyKind.SecretRef, Required = true },
            ],
        },
        new()
        {
            Key = "auth.apiKey",
            Group = NodeGroup.Auth,
            Icon = "key-square",
            Outputs = [Port.Out, TokenOut],
            Properties =
            [
                new()
                {
                    Name = "placement", LabelKey = "nodeprop.placement", Kind = PropertyKind.Choice,
                    Options = ["header", "query", "cookie"], Default = "header", Required = true,
                },
                new() { Name = "name", LabelKey = "nodeprop.name", Kind = PropertyKind.Text, Required = true, Placeholder = "X-Api-Key" },
                new() { Name = "value", LabelKey = "nodeprop.value", Kind = PropertyKind.SecretRef, Required = true },
            ],
        },
        new()
        {
            Key = "auth.oauth2ClientCredentials",
            Group = NodeGroup.Auth,
            Icon = "shield-check",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, TokenOut],
            Properties =
            [
                new() { Name = "tokenUrl", LabelKey = "nodeprop.tokenUrl", Kind = PropertyKind.Url, Required = true },
                new() { Name = "clientId", LabelKey = "nodeprop.clientId", Kind = PropertyKind.Text, Required = true },
                new() { Name = "clientSecret", LabelKey = "nodeprop.clientSecret", Kind = PropertyKind.SecretRef, Required = true },
                new() { Name = "scope", LabelKey = "nodeprop.oauthScope", Kind = PropertyKind.Text },
                new()
                {
                    Name = "cache", LabelKey = "nodeprop.cacheToken", Kind = PropertyKind.Boolean,
                    Default = "true", HelpKey = "nodeprop.cacheToken.help",
                },
            ],
        },
        new()
        {
            Key = "auth.oauth2Password",
            Group = NodeGroup.Auth,
            Icon = "shield-check",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, TokenOut],
            Properties =
            [
                new() { Name = "tokenUrl", LabelKey = "nodeprop.tokenUrl", Kind = PropertyKind.Url, Required = true },
                new() { Name = "username", LabelKey = "nodeprop.username", Kind = PropertyKind.Text, Required = true },
                new() { Name = "password", LabelKey = "nodeprop.password", Kind = PropertyKind.SecretRef, Required = true },
                new() { Name = "clientId", LabelKey = "nodeprop.clientId", Kind = PropertyKind.Text },
                new() { Name = "scope", LabelKey = "nodeprop.oauthScope", Kind = PropertyKind.Text },
            ],
        },
        new()
        {
            Key = "auth.oauth2Refresh",
            Group = NodeGroup.Auth,
            Icon = "refresh-cw",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, TokenOut],
            Properties =
            [
                new() { Name = "tokenUrl", LabelKey = "nodeprop.tokenUrl", Kind = PropertyKind.Url, Required = true },
                new() { Name = "refreshToken", LabelKey = "nodeprop.refreshToken", Kind = PropertyKind.SecretRef, Required = true },
                new() { Name = "clientId", LabelKey = "nodeprop.clientId", Kind = PropertyKind.Text },
            ],
        },
        new()
        {
            Key = "auth.login",
            Group = NodeGroup.Auth,
            Icon = "log-in",
            Reaches = true,
            Outputs = [Port.Out, Port.Failure, TokenOut, Port.Data("response", DataType.Response)],
            Properties =
            [
                new() { Name = "url", LabelKey = "nodeprop.url", Kind = PropertyKind.Url, Required = true },
                new() { Name = "username", LabelKey = "nodeprop.username", Kind = PropertyKind.Text, Required = true },
                new() { Name = "password", LabelKey = "nodeprop.password", Kind = PropertyKind.SecretRef, Required = true },
                new()
                {
                    Name = "tokenPath", LabelKey = "nodeprop.tokenPath", Kind = PropertyKind.JsonPath,
                    Required = true, Default = "$.accessToken", HelpKey = "nodeprop.tokenPath.help",
                },
            ],
        },
        new()
        {
            Key = "auth.setHeader",
            Group = NodeGroup.Auth,
            Icon = "list",
            Inputs = [Port.In, Port.Data("token", DataType.Secret) with { Required = true }],
            Properties =
            [
                new() { Name = "header", LabelKey = "nodeprop.header", Kind = PropertyKind.Text, Required = true, Default = "Authorization" },
                new() { Name = "prefix", LabelKey = "nodeprop.prefix", Kind = PropertyKind.Text, Default = "Bearer " },
                new()
                {
                    Name = "scope", LabelKey = "nodeprop.headerScope", Kind = PropertyKind.Choice,
                    Options = ["run", "branch"], Default = "run", HelpKey = "nodeprop.headerScope.help",
                },
            ],
        },
        new()
        {
            Key = "auth.cookieJar",
            Group = NodeGroup.Auth,
            Icon = "cookie",
            Properties =
            [
                new()
                {
                    Name = "action", LabelKey = "nodeprop.cookieAction", Kind = PropertyKind.Choice,
                    Options = ["keep", "clear"], Default = "keep", Required = true,
                    HelpKey = "nodeprop.cookieAction.help",
                },
            ],
        },
        new()
        {
            Key = "auth.signHmac",
            Group = NodeGroup.Auth,
            Icon = "fingerprint",
            Outputs = [Port.Out, Port.Data("signature", DataType.Text)],
            Properties =
            [
                new() { Name = "payload", LabelKey = "nodeprop.payload", Kind = PropertyKind.LongText, Required = true },
                new() { Name = "secret", LabelKey = "nodeprop.signingSecret", Kind = PropertyKind.SecretRef, Required = true },
                new()
                {
                    Name = "algorithm", LabelKey = "nodeprop.algorithm", Kind = PropertyKind.Choice,
                    Options = ["sha256", "sha512"], Default = "sha256",
                },
            ],
        },
    ];
}
