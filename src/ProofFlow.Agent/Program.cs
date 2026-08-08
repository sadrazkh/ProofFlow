using System.Net.Http.Json;
using System.Text.Json;
using ProofFlow.Agent;
using ProofFlow.Contracts.Runners;

// The agent that runs inside somebody else's network.
//
// Two commands and nothing else:
//
//   proofflow-agent enroll --url https://proofflow.example.com --code ABCD-EFGH-JKMN-PQRS
//   proofflow-agent run    --url https://proofflow.example.com
//
// Enrolling writes the credentials next to the executable and prints nothing secret. Running polls,
// verifies the signature on whatever it is handed, executes it, and reports.
//
// It never listens on a port. Nothing connects to an agent — there is nothing to connect to, which
// is the whole reason for having one.

var command = args.FirstOrDefault();
var options = Options.Read(args);

switch (command)
{
    case "enroll":
        return await EnrollAsync(options);

    case "run":
        return await RunAsync(options);

    default:
        Console.Error.WriteLine(
            """
            proofflow-agent

              enroll --url <address> --code <code>   Redeem an enrollment code, once.
              run    --url <address>                 Poll for work and run it.

            The address is where ProofFlow is; the code comes from its Runners page.
            """);
        return 1;
}

static async Task<int> EnrollAsync(Options options)
{
    if (options.Url is null || options.Code is null)
    {
        Console.Error.WriteLine("Both --url and --code are needed to enrol.");
        return 1;
    }

    using var client = new HttpClient { BaseAddress = new Uri(options.Url) };

    var response = await client.PostAsJsonAsync("/api/v1/runners/enroll", new EnrollRequest
    {
        Code = options.Code,
        Hostname = Environment.MachineName,
        Version = Credentials.AgentVersion,
    });

    if (!response.IsSuccessStatusCode)
    {
        // One message, because the server gives one answer: a code that is unknown, spent or
        // expired are the same reply on purpose.
        Console.Error.WriteLine("That code is not usable. Issue a fresh one and try again.");
        return 1;
    }

    var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>();

    if (enrolled is null)
    {
        Console.Error.WriteLine("The server answered with something this cannot read.");
        return 1;
    }

    await Credentials.WriteAsync(options.Url, enrolled);

    // Deliberately says nothing about the token or the key. A terminal is scrolled back through,
    // copied into a ticket, and pasted into a chat.
    Console.WriteLine($"Enrolled as «{enrolled.Name}». Credentials written to {Credentials.Path}.");
    Console.WriteLine("Run «proofflow-agent run» to start taking work.");

    return 0;
}

static async Task<int> RunAsync(Options options)
{
    var credentials = await Credentials.ReadAsync();

    if (credentials is null)
    {
        Console.Error.WriteLine("Not enrolled. Run «proofflow-agent enroll» first.");
        return 1;
    }

    var address = options.Url ?? credentials.Url;

    using var client = new HttpClient { BaseAddress = new Uri(address) };
    client.DefaultRequestHeaders.Add("X-ProofFlow-Runner", credentials.Token);

    using var stopping = new CancellationTokenSource();

    Console.CancelKeyPress += (_, key) =>
    {
        // Finish what is in hand rather than dropping it. A run that stops halfway leaves somebody
        // looking at a page that never changes.
        key.Cancel = true;
        stopping.Cancel();
        Console.WriteLine("Stopping after the current job.");
    };

    Console.WriteLine($"Watching {address} as «{credentials.Name}». Ctrl+C to stop.");

    var poll = TimeSpan.FromSeconds(Math.Max(5, credentials.PollSeconds));

    while (!stopping.IsCancellationRequested)
    {
        try
        {
            var claimed = await client.PostAsync("/api/v1/runners/jobs/claim", null, stopping.Token);

            if (claimed.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                await Task.Delay(poll, stopping.Token);
                continue;
            }

            if (!claimed.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"The server answered {(int)claimed.StatusCode} to a claim.");
                await Task.Delay(poll, stopping.Token);
                continue;
            }

            var job = await claimed.Content.ReadFromJsonAsync<SignedJob>(stopping.Token);

            if (job is null) continue;

            // The one check that makes this safe to do at all. An agent runs arbitrary requests
            // against machines inside a private network; if it cannot prove where the instruction
            // came from, it should not carry it out.
            if (!JobSignature.Verify(job.Payload, job.Signature, credentials.SigningKey))
            {
                Console.Error.WriteLine(
                    $"Refused job {job.JobId}: the signature does not match. Something between " +
                    "here and ProofFlow altered it, or this agent is enrolled against a different " +
                    "installation.");

                continue;
            }

            Console.WriteLine($"Running job {job.JobId}.");

            var result = await JobRunner.ExecuteAsync(job, stopping.Token);

            await client.PostAsJsonAsync("/api/v1/runners/jobs/result", result, stopping.Token);

            Console.WriteLine(
                $"Reported {result.Status} in {result.DurationMs:0}ms "
                + $"({result.Steps} steps, {result.AssertionsPassed} checks passed, "
                + $"{result.AssertionsFailed} failed).");
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception exception)
        {
            // Never fatal. An agent that exits on the first network hiccup is an agent somebody has
            // to babysit, and the machine it runs on is usually one nobody logs into.
            Console.Error.WriteLine($"That round did not work out: {exception.Message}");

            try
            {
                await Task.Delay(poll, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    Console.WriteLine("Stopped.");
    return 0;
}

namespace ProofFlow.Agent
{
    /// <summary>The flags, read plainly. An agent has three of them and does not need a parser.</summary>
    internal sealed record Options(string? Url, string? Code)
    {
        public static Options Read(string[] args)
        {
            string? Value(string name)
            {
                var at = Array.IndexOf(args, name);
                return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            }

            return new Options(Value("--url")?.TrimEnd('/'), Value("--code"));
        }
    }

    /// <summary>
    /// What the agent knows about itself, on disk.
    ///
    /// Next to the executable rather than in a home directory, because this runs as a service on a
    /// machine nobody logs into. It holds a token and a signing key, which is a real cost of this
    /// design and worth stating plainly: the file is written with permissions for its owner alone,
    /// and the credentials it holds are scoped to one runner and revocable from the interface in one
    /// click.
    /// </summary>
    internal sealed record Credentials(
        string Url, Guid RunnerId, string Name, string Token, string SigningKey, int PollSeconds)
    {
        public const string AgentVersion = "1.0.0";

        public static string Path =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "runner.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public static async Task WriteAsync(string url, EnrollResponse enrolled)
        {
            var credentials = new Credentials(
                url, enrolled.RunnerId, enrolled.Name, enrolled.Token,
                enrolled.SigningKey, enrolled.PollSeconds);

            await File.WriteAllTextAsync(Path, JsonSerializer.Serialize(credentials, Json));

            if (!OperatingSystem.IsWindows())
            {
                // 0600. On Windows the file inherits the directory's ACL, which for a service
                // directory is already what is wanted.
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public static async Task<Credentials?> ReadAsync()
        {
            if (!File.Exists(Path)) return null;

            try
            {
                return JsonSerializer.Deserialize<Credentials>(
                    await File.ReadAllTextAsync(Path), Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
