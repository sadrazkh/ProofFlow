namespace ProofFlow.Contracts.Runners;

/// <summary>
/// Everything an agent needs to run one scenario, and nothing else.
///
/// The list is short on purpose and was arrived at by reading what the engine actually asks for:
/// send this request, give me those rows, what was approved for this input. So the package carries
/// the graph, the environment, the variables, the secrets those variables reference, the data sets
/// and baselines the graph names — and stops.
///
/// It does not carry the project, the workspace, the team, other environments, or anything else the
/// database happens to hold. An agent lives on somebody's internal network; the less of a customer's
/// installation is copied onto it, the less there is to lose when that machine is the one that gets
/// compromised.
/// </summary>
public sealed record JobPackage
{
    public required Guid RunId { get; init; }

    public required string ScenarioName { get; init; }

    /// <summary>The graph as it stood when the run was queued, not as it stands now.</summary>
    public required string Definition { get; init; }

    public JobEnvironment? Environment { get; init; }

    /// <summary>
    /// The variables and secrets, already resolved into one map.
    ///
    /// Resolved on the server because that is where the cipher's master key is. The agent receives
    /// values it can use and never a key it could decrypt anything else with — and the values travel
    /// only over the same TLS connection the job did, to a machine that already had to enrol.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Secrets { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The data sets the graph names, by the name the graph uses.</summary>
    public IReadOnlyList<JobDataSet> DataSets { get; init; } = [];

    /// <summary>The baselines the graph names, with their approved answer and rules.</summary>
    public IReadOnlyList<JobBaseline> Baselines { get; init; } = [];

    /// <summary>The step to begin at, or null for the whole scenario. Travels so a partial run is
    /// partial on the agent too, rather than quietly becoming a whole one out there.</summary>
    public string? StartNodeId { get; init; }

    /// <summary>
    /// What this run was told, already settled — supplied values with defaults filled in.
    ///
    /// Settled on this side rather than sent as definitions plus answers, so the agent cannot reach
    /// a different conclusion about what a missing value means.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();
}

public sealed record JobEnvironment
{
    public string? Name { get; init; }
    public string? BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxRedirects { get; init; } = 5;
    public int MaxResponseKilobytes { get; init; } = 4096;
    public string? AllowedHosts { get; init; }
    public string? DeniedHosts { get; init; }
    public bool AllowPrivateNetwork { get; init; }
    public bool AllowInvalidCertificate { get; init; }
    public string? DefaultHeadersJson { get; init; }

    /// <summary>
    /// How this environment authenticates, so the agent signs in itself.
    ///
    /// The configuration rather than a token: a token fetched when the job was queued may be dead
    /// by the time an agent picks the job up, and a run that fails with 401 on a network nobody can
    /// see from here is the worst kind of failure to explain.
    ///
    /// It carries references — <c>{{secrets.apiPassword}}</c> — resolved against the secrets that
    /// already travel in this package, so nothing crosses the wire that did not cross it before.
    /// </summary>
    public string? AuthenticationJson { get; init; }
}

public sealed record JobDataSet
{
    public required string Name { get; init; }
    public required Guid Id { get; init; }

    /// <summary>Each row's values as a JSON object, in order, enabled ones only.</summary>
    public IReadOnlyList<string> Rows { get; init; } = [];
}

public sealed record JobBaseline
{
    public required string Name { get; init; }
    public required Guid Id { get; init; }

    /// <summary>The approved whole-response answer, when there is one.</summary>
    public string? ApprovedBody { get; init; }

    /// <summary>The comparison rules, as the same JSON the baseline page stores.</summary>
    public string? RulesJson { get; init; }

    /// <summary>Approved sample answers, by key, for a sample-based baseline.</summary>
    public IReadOnlyDictionary<string, string> Samples { get; init; } =
        new Dictionary<string, string>();
}

// ------------------------------------------------------------------------------------------------

/// <summary>
/// What the agent sends back: the verdict, and everything the console needs to show what happened.
///
/// The agent has no database, so it reports rather than writes. That is the seam — one place where
/// a remote run and a local run differ, and it is a transport difference rather than a behavioural
/// one, because both used the same engine to get here.
/// </summary>
public sealed record JobReport
{
    public required Guid JobId { get; init; }
    public required string Status { get; init; }
    public string? Outcome { get; init; }
    public int Steps { get; init; }
    public int StepsFailed { get; init; }
    public int AssertionsPassed { get; init; }
    public int AssertionsFailed { get; init; }
    public double DurationMs { get; init; }

    public IReadOnlyList<JobNodeResult> Nodes { get; init; } = [];
    public IReadOnlyList<JobLogLine> Log { get; init; } = [];

    /// <summary>Answers the run captured, for the server to file into the review queue.</summary>
    public IReadOnlyList<JobCapture> Captures { get; init; } = [];
}

public sealed record JobNodeResult
{
    public required string NodeId { get; init; }
    public required string NodeKey { get; init; }
    public required string NodeName { get; init; }
    public int Iteration { get; init; }
    public int Attempt { get; init; } = 1;
    public required string Status { get; init; }
    public double DurationMs { get; init; }
    public string? TakenPort { get; init; }
    public string? OutputJson { get; init; }
    public string? FailureMessage { get; init; }
    public int SortOrder { get; init; }

    public IReadOnlyList<JobAssertion> Assertions { get; init; } = [];
}

public sealed record JobAssertion
{
    public required string Description { get; init; }
    public bool Passed { get; init; }
    public bool Soft { get; init; }
    public string? Target { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
}

public sealed record JobLogLine
{
    public long Sequence { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? NodeId { get; init; }
    public string? NodeName { get; init; }
}

public sealed record JobCapture
{
    public required string Baseline { get; init; }
    public string? Key { get; init; }
    public required string Body { get; init; }
    public string? ContentType { get; init; }
    public int StatusCode { get; init; }
    public string? Url { get; init; }
    public double DurationMs { get; init; }
    public bool Approve { get; init; }
}
