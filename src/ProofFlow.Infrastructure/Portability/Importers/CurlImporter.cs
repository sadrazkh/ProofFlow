using System.Text;
using ProofFlow.TestEngine.Http;

namespace ProofFlow.Infrastructure.Portability.Importers;

/// <summary>
/// Turns a cURL command into a request.
///
/// This is the smallest useful door into the product, and the one people actually walk through:
/// every browser's network panel offers "copy as cURL", so a person with a working call in their
/// hands is one paste away from a test of it.
///
/// The parser is deliberately a parser rather than a regular expression. A cURL line is a shell
/// command: it has quoting, escapes, and line continuations, and a URL with an ampersand in it will
/// be inside quotes. Splitting on whitespace produces something that works on the examples in a
/// blog post and fails on the first real one.
/// </summary>
public static class CurlImporter
{
    /// <summary>The longest command this will look at. A paste, not a file.</summary>
    public const int MaxLength = 256 * 1024;

    public static Imported Read(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Imported.Refused("import.empty");
        if (command.Length > MaxLength) return Imported.Refused("import.tooLarge");

        var words = Split(command);

        if (words.Count == 0 || !words[0].Equals("curl", StringComparison.OrdinalIgnoreCase))
        {
            return Imported.Refused("import.notCurl");
        }

        var method = (string?)null;
        var url = (string?)null;
        var headers = new List<KeyValueEntry>();
        var form = new List<KeyValueEntry>();
        var secrets = new List<string>();
        var notes = new List<string>();

        string? body = null;
        string? contentType = null;
        AuthenticationSpec? authentication = null;
        var getWithData = false;

        for (var at = 1; at < words.Count; at++)
        {
            var word = words[at];

            switch (word)
            {
                case "-X" or "--request":
                    method = Next(words, ref at)?.ToUpperInvariant();
                    break;

                case "--url":
                    url = Next(words, ref at);
                    break;

                case "-H" or "--header":
                    Header(Next(words, ref at), headers, secrets);
                    break;

                case "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-ascii":
                    body = Append(body, Next(words, ref at));
                    break;

                case "--json":
                    body = Append(body, Next(words, ref at));
                    contentType = "application/json";
                    break;

                case "-F" or "--form":
                    Field(Next(words, ref at), form);
                    break;

                case "-u" or "--user":
                    // Basic credentials, in the clear, in a command somebody pasted. The username
                    // is a name; the password is not kept.
                    var pair = Next(words, ref at) ?? string.Empty;
                    var colon = pair.IndexOf(':');

                    authentication = new AuthenticationSpec
                    {
                        Kind = AuthenticationKind.Basic,
                        Username = colon >= 0 ? pair[..colon] : pair,
                        Password = "{{secrets.password}}",
                    };

                    secrets.Add("password");
                    notes.Add("import.note.basicPassword");
                    break;

                case "-b" or "--cookie":
                    _ = Next(words, ref at);
                    notes.Add("import.note.cookie");
                    break;

                case "-G" or "--get":
                    getWithData = true;
                    break;

                case "-k" or "--insecure":
                    // Recorded rather than obeyed. Turning off certificate validation is a decision
                    // about an environment, made on the environment page, by somebody who meant it.
                    notes.Add("import.note.insecure");
                    break;

                case "-L" or "--location" or "--compressed" or "-s" or "--silent"
                    or "-i" or "--include" or "-v" or "--verbose" or "-#" or "--progress-bar":
                    break;

                case "-o" or "--output" or "-w" or "--write-out" or "-A" or "--user-agent"
                    or "-e" or "--referer" or "--max-time" or "--connect-timeout" or "--retry":
                    _ = Next(words, ref at);
                    break;

                default:
                    if (word.StartsWith('-'))
                    {
                        // An unknown flag is reported rather than guessed at. Consuming a value it
                        // does not take would eat the URL.
                        notes.Add("import.note.unknownFlag");
                        break;
                    }

                    url ??= word;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(url)) return Imported.Refused("import.noUrl");

        var kind = BodyFor(body, form, contentType, out var payload);

        if (getWithData && payload?.Kind is BodyKind.FormUrlEncoded or BodyKind.Text)
        {
            // -G moves the data into the query string. Said out loud rather than silently applied,
            // because the request that results is a different request.
            notes.Add("import.note.getWithData");
        }

        return new Imported
        {
            SuggestedName = Name(url),
            BaseUrl = Origin(url),
            SecretsToSupply = [.. secrets.Distinct(StringComparer.Ordinal)],
            Notes = [.. notes.Distinct(StringComparer.Ordinal)],
            Requests =
            [
                new ImportedRequest
                {
                    Name = Name(url),
                    Request = new HttpRequestDefinition
                    {
                        Method = method ?? (kind == BodyKind.None ? "GET" : "POST"),
                        Url = url,
                        Headers = headers,
                        Body = payload,
                        Authentication = authentication,
                    },
                },
            ],
        };
    }

    private static string? Append(string? existing, string? addition) =>
        addition is null ? existing
        : existing is null ? addition
        : $"{existing}&{addition}";

    private static string? Next(List<string> words, ref int at) =>
        at + 1 < words.Count ? words[++at] : null;

    private static void Header(string? raw, List<KeyValueEntry> headers, List<string> secrets)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var colon = raw.IndexOf(':');
        if (colon <= 0) return;

        var name = raw[..colon].Trim();
        var value = raw[(colon + 1)..].Trim();

        if (Credentials.IsCredential(name))
        {
            headers.Add(new KeyValueEntry(name, Credentials.Reference(name)));
            secrets.Add(Credentials.SecretName(name));
            return;
        }

        headers.Add(new KeyValueEntry(name, value));
    }

    private static void Field(string? raw, List<KeyValueEntry> form)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var equals = raw.IndexOf('=');
        if (equals <= 0) return;

        form.Add(new KeyValueEntry(raw[..equals].Trim(), raw[(equals + 1)..].Trim()));
    }

    private static BodyKind BodyFor(
        string? body, List<KeyValueEntry> form, string? contentType, out RequestBody? payload)
    {
        if (form.Count > 0)
        {
            payload = new RequestBody { Kind = BodyKind.Multipart, Form = form };
            return BodyKind.Multipart;
        }

        // Null means no -d at all. Empty means -d '' — which curl sends as a POST with an empty
        // body, and turning that into a GET would be a different request.
        if (body is null)
        {
            payload = null;
            return BodyKind.None;
        }

        if (body.Length == 0)
        {
            payload = new RequestBody { Kind = BodyKind.Text, Content = string.Empty };
            return BodyKind.Text;
        }

        // JSON when it looks like JSON, which is the case worth getting right: everything else is
        // sent as text and reads correctly either way.
        var trimmed = body.TrimStart();

        var kind = contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                   || trimmed.StartsWith('{') || trimmed.StartsWith('[')
            ? BodyKind.Json
            : body.Contains('=') && !body.Contains('\n')
                ? BodyKind.FormUrlEncoded
                : BodyKind.Text;

        payload = new RequestBody { Kind = kind, Content = body, ContentType = contentType };
        return kind;
    }

    private static string Name(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var path = uri.AbsolutePath.Trim('/');

        return string.IsNullOrEmpty(path) ? uri.Host : path;
    }

    private static string? Origin(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : null;

    /// <summary>
    /// Splits a shell command the way a shell would.
    ///
    /// Single quotes take everything literally; double quotes honour backslash escapes; a backslash
    /// at the end of a line joins it to the next, which is how every "copy as cURL" writes a long
    /// command. None of that is optional — a URL with <c>&amp;</c> in it lives inside quotes, and a
    /// splitter that ignores them cuts it in half.
    /// </summary>
    internal static List<string> Split(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        var started = false;

        for (var at = 0; at < command.Length; at++)
        {
            var c = command[at];

            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }

                if (c == '\\' && quote == '"' && at + 1 < command.Length)
                {
                    word.Append(command[++at]);
                    continue;
                }

                word.Append(c);
                started = true;
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    started = true;
                    continue;

                case '\\' when at + 1 < command.Length && (command[at + 1] is '\n' or '\r'):
                    // A continuation. Skip the backslash and the newline that follows it.
                    while (at + 1 < command.Length && command[at + 1] is '\n' or '\r') at++;
                    continue;

                case '\\' when at + 1 < command.Length:
                    word.Append(command[++at]);
                    started = true;
                    continue;

                case ' ' or '\t' or '\n' or '\r':
                    if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                    continue;

                default:
                    word.Append(c);
                    started = true;
                    continue;
            }
        }

        if (started) words.Add(word.ToString());

        return words;
    }
}
