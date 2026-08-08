using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Portability;
using ProofFlow.Contracts.Scenarios;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Scenarios;

namespace ProofFlow.Infrastructure.Portability;

/// <summary>
/// Writes a project out as a file somebody can commit.
///
/// Two properties matter more than anything else here, and both are about the file being read by a
/// person and by <c>git diff</c> rather than only by this program.
///
/// It is <b>deterministic</b>: everything is ordered by name or slug, and node identifiers are
/// renumbered n1…nN in draw order. Export the same unchanged project twice and the two files match
/// except for the timestamp. Move one node and one line changes.
///
/// It contains <b>no secret</b>. Not the value, not the ciphertext. Only the names, so the far side
/// knows what it has to create.
/// </summary>
public sealed class BundleExporter(ProofFlowDbContext db, ScenarioGraphService graphs, IClock clock)
{
    public async Task<Bundle> ExportAsync(Guid projectId, CancellationToken cancellation = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellation)
            ?? throw new InvalidOperationException("That project does not exist.");

        var environments = await db.Environments
            .Where(environment => environment.ProjectId == projectId)
            .OrderBy(environment => environment.SortOrder)
            .ThenBy(environment => environment.Slug)
            .ToListAsync(cancellation);

        var bySlug = environments.ToDictionary(environment => environment.Id, e => e.Slug);

        var variables = await db.Variables
            .Where(variable => variable.ProjectId == projectId)
            .OrderBy(variable => variable.Name)
            .ToListAsync(cancellation);

        return new Bundle
        {
            ExportedAt = clock.UtcNow,

            Project = new BundleProject
            {
                Name = project.Name,
                Slug = project.Slug,
                Description = project.Description,
                Accent = project.Accent,
            },

            Environments =
            [
                .. environments.Select(environment => new BundleEnvironment
                {
                    Slug = environment.Slug,
                    Name = environment.Name,
                    Kind = environment.Kind.ToString(),
                    BaseUrl = environment.BaseUrl,
                    IsProduction = environment.IsProduction,
                    TimeoutSeconds = environment.TimeoutSeconds,
                    MaxRedirects = environment.MaxRedirects,
                    MaxResponseKilobytes = environment.MaxResponseKilobytes,
                    AllowedHosts = environment.AllowedHosts,
                    AllowPrivateNetwork = environment.AllowPrivateNetwork,
                    AllowInvalidCertificate = environment.AllowInvalidCertificate,
                    DefaultHeadersJson = environment.DefaultHeadersJson,
                    SortOrder = environment.SortOrder,
                    Variables =
                    [
                        .. variables
                            .Where(variable => variable.EnvironmentId == environment.Id)
                            .Select(variable => new BundleVariable
                            {
                                Name = variable.Name,
                                Value = variable.Value,
                                Description = variable.Description,
                            }),
                    ],
                }),
            ],

            Scenarios = await ScenariosAsync(projectId, bySlug, cancellation),
            Baselines = await BaselinesAsync(projectId, bySlug, cancellation),
            DataSets = await DataSetsAsync(projectId, cancellation),
            Schedules = await SchedulesAsync(projectId, bySlug, cancellation),

            // Names only, and this is the one place in the codebase where that is the entire point.
            SecretsToSupply =
            [
                .. (await db.Secrets
                        .Where(secret => secret.ProjectId == projectId)
                        .OrderBy(secret => secret.Name)
                        .Select(secret => new { secret.Name, secret.EnvironmentId, secret.Description })
                        .ToListAsync(cancellation))
                    .Select(secret => new BundleSecretName
                    {
                        Name = secret.Name,
                        Environment = secret.EnvironmentId is { } id ? bySlug.GetValueOrDefault(id) : null,
                        Description = secret.Description,
                    }),
            ],
        };
    }

    private async Task<IReadOnlyList<BundleScenario>> ScenariosAsync(
        Guid projectId, Dictionary<Guid, string> environments, CancellationToken cancellation)
    {
        var scenarios = await db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId && scenario.ArchivedAt == null)
            .OrderBy(scenario => scenario.Name)
            .ToListAsync(cancellation);

        var exported = new List<BundleScenario>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            // Published if there is one, draft otherwise. What travels is what this project would
            // run tonight; a scenario nobody has published yet still travels, because a suite
            // somebody is halfway through building is worth moving between machines.
            var versionId = scenario.PublishedVersionId ?? scenario.DraftVersionId;
            if (versionId is not { } id) continue;

            var graph = await graphs.LoadAsync(id, cancellation);

            exported.Add(new BundleScenario
            {
                Slug = Slug.From(scenario.Name, "scenario"),
                Name = scenario.Name,
                Description = scenario.Description,
                Environment = scenario.EnvironmentId is { } environmentId
                    ? environments.GetValueOrDefault(environmentId)
                    : null,
                Graph = Renumber(graph),
            });
        }

        return exported;
    }

    /// <summary>
    /// Replaces database identifiers with n1…nN and e1…eN, in draw order.
    ///
    /// Without this every export of the same graph is a different file, because the identifiers are
    /// GUIDs generated when the nodes were created. With it, two people who built the same scenario
    /// from the same template produce files that differ only where the scenarios differ.
    /// </summary>
    internal static GraphDto Renumber(GraphDto graph)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0;

        foreach (var node in graph.Nodes) names[node.Id] = $"n{++next}";

        var order = graph.Nodes
            .Select((node, index) => (node.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);

        var edges = 0;

        return new GraphDto
        {
            Nodes =
            [
                .. graph.Nodes.Select(node => node with
                {
                    Id = names[node.Id],
                    ParentId = node.ParentId is { } parent ? names.GetValueOrDefault(parent) : null,

                    // Ordered, so a property added today does not reorder every line tomorrow.
                    Properties = node.Properties
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                }),
            ],
            Edges =
            [
                // Sorted by where they start and end rather than left in the order the database
                // happened to return. Nothing orders that column, so two projects with identical
                // graphs could otherwise produce files that differ in nothing but line order —
                // which is exactly the kind of diff that makes people stop reading diffs.
                .. graph.Edges
                    .Where(edge => names.ContainsKey(edge.FromId) && names.ContainsKey(edge.ToId))
                    .OrderBy(edge => order[edge.FromId])
                    .ThenBy(edge => edge.FromPort, StringComparer.Ordinal)
                    .ThenBy(edge => order[edge.ToId])
                    .ThenBy(edge => edge.ToPort, StringComparer.Ordinal)
                    .Select(edge => edge with
                    {
                        Id = $"e{++edges}",
                        FromId = names[edge.FromId],
                        ToId = names[edge.ToId],
                    }),
            ],

            // Dropped on purpose. The viewport is where one person happened to be looking.
            CanvasJson = null,
        };
    }

    private async Task<IReadOnlyList<BundleBaseline>> BaselinesAsync(
        Guid projectId, Dictionary<Guid, string> environments, CancellationToken cancellation)
    {
        var baselines = await db.Baselines
            .Where(baseline => baseline.ProjectId == projectId && baseline.ArchivedAt == null)
            .OrderBy(baseline => baseline.Name)
            .ToListAsync(cancellation);

        var approvedIds = baselines
            .Select(baseline => baseline.ApprovedVersionId)
            .OfType<Guid>()
            .ToList();

        var approved = await db.BaselineVersions
            .Where(version => approvedIds.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, cancellation);

        return
        [
            .. baselines.Select(baseline => new BundleBaseline
            {
                Slug = Slug.From(baseline.Name, "baseline"),
                Name = baseline.Name,
                Description = baseline.Description,
                Environment = baseline.EnvironmentId is { } id
                    ? environments.GetValueOrDefault(id)
                    : null,
                RequestJson = baseline.RequestJson,
                Approved = Version(baseline, approved),
            }),
        ];

        static BundleBaselineVersion? Version(
            Baseline baseline, Dictionary<Guid, BaselineVersion> approved) =>
            baseline.ApprovedVersionId is { } id && approved.TryGetValue(id, out var version)
                ? new BundleBaselineVersion
                {
                    Body = version.Body,
                    ContentType = version.ContentType,
                    StatusCode = version.StatusCode,
                    HeadersJson = version.HeadersJson,
                    RulesJson = version.RulesJson,
                    Description = version.Description,
                }
                : null;
    }

    private async Task<IReadOnlyList<BundleDataSet>> DataSetsAsync(
        Guid projectId, CancellationToken cancellation)
    {
        var sets = await db.DataSets
            .Where(set => set.ProjectId == projectId && set.ArchivedAt == null)
            .OrderBy(set => set.Name)
            .ToListAsync(cancellation);

        var exported = new List<BundleDataSet>(sets.Count);

        foreach (var set in sets)
        {
            // The current version only. Earlier ones are the history of one team's edits.
            var version = set.CurrentVersionId is { } id
                ? await db.DataSetVersions.FirstOrDefaultAsync(v => v.Id == id, cancellation)
                : null;

            var rows = version is null
                ? []
                : await db.DataSetRows
                    .Where(row => row.DataSetVersionId == version.Id)
                    .OrderBy(row => row.Ordinal)
                    .Select(row => new BundleDataRow
                    {
                        Key = row.Key,
                        ValuesJson = row.ValuesJson,
                        Enabled = row.Enabled,
                    })
                    .ToListAsync(cancellation);

            exported.Add(new BundleDataSet
            {
                Slug = Slug.From(set.Name, "dataset"),
                Name = set.Name,
                Description = set.Description,
                KeyColumn = set.KeyColumn,
                ColumnsJson = version?.ColumnsJson,
                Rows = rows,
            });
        }

        return exported;
    }

    private async Task<IReadOnlyList<BundleSchedule>> SchedulesAsync(
        Guid projectId, Dictionary<Guid, string> environments, CancellationToken cancellation)
    {
        var schedules = await db.RunSchedules
            .Where(schedule => schedule.ProjectId == projectId)
            .OrderBy(schedule => schedule.Name)
            .ToListAsync(cancellation);

        if (schedules.Count == 0) return [];

        var ids = schedules.Select(schedule => schedule.Id).ToList();

        var chosenScenarios = await db.ScheduleScenarios
            .Where(link => ids.Contains(link.RunScheduleId))
            .Join(db.Scenarios, link => link.ScenarioId, scenario => scenario.Id,
                (link, scenario) => new { link.RunScheduleId, scenario.Name })
            .ToListAsync(cancellation);

        var chosenEnvironments = await db.ScheduleEnvironments
            .Where(link => ids.Contains(link.RunScheduleId))
            .ToListAsync(cancellation);

        return
        [
            .. schedules.Select(schedule => new BundleSchedule
            {
                Name = schedule.Name,
                Cron = schedule.Cron,
                TimeZone = schedule.TimeZoneId,
                Enabled = schedule.Enabled,
                Scenarios =
                [
                    .. chosenScenarios
                        .Where(link => link.RunScheduleId == schedule.Id)
                        .Select(link => Slug.From(link.Name, "scenario"))
                        .OrderBy(slug => slug, StringComparer.Ordinal),
                ],
                Environments =
                [
                    .. chosenEnvironments
                        .Where(link => link.RunScheduleId == schedule.Id)
                        .Select(link => environments.GetValueOrDefault(link.EnvironmentId))
                        .OfType<string>()
                        .OrderBy(slug => slug, StringComparer.Ordinal),
                ],
            }),
        ];
    }
}
