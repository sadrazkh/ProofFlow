using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Contracts.Portability;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Data;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Scenarios;

namespace ProofFlow.Infrastructure.Portability;

/// <summary>
/// Reads a bundle into a project.
///
/// <b>An import adds. It never overwrites.</b> Anything whose slug is already taken is left exactly
/// as it is and counted as skipped, and the preview says so before anybody presses anything.
///
/// That rule is the whole safety model here. The alternative — merging, or replacing what matches —
/// means a file somebody was handed can silently change a baseline that a schedule runs against
/// production tonight, and there is no undo for that. Somebody who genuinely wants the incoming
/// version can delete theirs and import again; somebody who did not would otherwise have no way
/// back.
/// </summary>
public sealed class BundleImporter(
    ProofFlowDbContext db,
    ScenarioGraphService graphs,
    ICurrentUser me,
    IClock clock)
{
    /// <summary>
    /// What the import would do, without doing any of it.
    ///
    /// Reads the same collision rules the apply uses — from the same methods — so the counts cannot
    /// disagree with what happens next.
    /// </summary>
    public async Task<ImportPreview> PreviewAsync(
        Bundle bundle, Guid? projectId, CancellationToken cancellation = default)
    {
        var project = projectId is { } id
            ? await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellation)
            : null;

        if (projectId is not null && project is null)
        {
            return new ImportPreview
            {
                ProjectName = bundle.Project.Name,
                Counts = [],
                Refusal = "import.noSuchProject",
            };
        }

        var taken = project is null ? new Taken() : await TakenAsync(project.Id, cancellation);
        var skipped = new List<string>();

        var counts = new List<ImportCount>
        {
            Count("environment", bundle.Environments.Select(e => e.Slug), taken.Environments, skipped),
            Count("scenario", bundle.Scenarios.Select(s => s.Slug), taken.Scenarios, skipped),
            Count("baseline", bundle.Baselines.Select(b => b.Slug), taken.Baselines, skipped),
            Count("dataset", bundle.DataSets.Select(d => d.Slug), taken.DataSets, skipped),
            Count("schedule", bundle.Schedules.Select(s => Slug.From(s.Name, "schedule")), taken.Schedules, skipped),
        };

        return new ImportPreview
        {
            ProjectName = project?.Name ?? bundle.Project.Name,
            CreatesProject = project is null,
            Counts = [.. counts.Where(count => count.Adding > 0 || count.Existing > 0)],
            Skipped = skipped,
            SecretsToSupply = [.. bundle.SecretsToSupply.Select(secret => secret.Name)],
        };

        static ImportCount Count(
            string kind, IEnumerable<string> slugs, HashSet<string> taken, List<string> skipped)
        {
            var all = slugs.ToList();
            var existing = all.Where(taken.Contains).ToList();

            skipped.AddRange(existing);

            return new ImportCount(kind, all.Count - existing.Count, existing.Count);
        }
    }

    /// <summary>
    /// Carries the import out, in one transaction.
    ///
    /// One transaction because a half-imported project is worse than a refused import: scenarios
    /// pointing at environments that were not created, schedules pointing at scenarios that were
    /// not, and nothing to tell somebody which half arrived.
    /// </summary>
    public async Task<ImportResult> ApplyAsync(
        Bundle bundle, Guid? projectId, CancellationToken cancellation = default)
    {
        var workspaceId = me.WorkspaceId
            ?? throw new InvalidOperationException("An import needs a workspace.");

        var project = projectId is { } id
            ? await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellation)
                ?? throw new InvalidOperationException("That project does not exist.")
            : await NewProjectAsync(workspaceId, bundle, cancellation);

        var taken = await TakenAsync(project.Id, cancellation);
        var skipped = new List<string>();

        var environments = await EnvironmentsAsync(project, bundle, taken, skipped, cancellation);
        var scenarios = await ScenariosAsync(project, bundle, taken, environments, skipped, cancellation);
        var baselines = BaselinesFrom(project, bundle, taken, environments, skipped);
        var dataSets = DataSetsFrom(project, bundle, taken, skipped);

        await db.SaveChangesAsync(cancellation);

        // After the scenarios exist, because a schedule points at them by name.
        var schedules = await SchedulesAsync(project, bundle, taken, environments, skipped, cancellation);

        await db.SaveChangesAsync(cancellation);

        return new ImportResult
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Counts =
            [
                new ImportCount("environment", environments.Added, environments.Existing),
                new ImportCount("scenario", scenarios, 0),
                new ImportCount("baseline", baselines, 0),
                new ImportCount("dataset", dataSets, 0),
                new ImportCount("schedule", schedules, 0),
            ],
            Skipped = skipped,
        };
    }

    // ---- the pieces --------------------------------------------------------------------------

    private async Task<Project> NewProjectAsync(
        Guid workspaceId, Bundle bundle, CancellationToken cancellation)
    {
        // A name that is already in use gets a number, rather than a second project that looks
        // exactly like the first one in every list.
        var wanted = Slug.From(bundle.Project.Slug ?? bundle.Project.Name, "project");
        var slug = wanted;
        var suffix = 1;

        while (await db.Projects.AnyAsync(p => p.Slug == slug, cancellation))
        {
            slug = $"{wanted}-{++suffix}";
        }

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = suffix == 1 ? bundle.Project.Name : $"{bundle.Project.Name} ({suffix})",
            Slug = slug,
            Description = bundle.Project.Description,
            Accent = bundle.Project.Accent ?? "indigo",
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellation);

        return project;
    }

    private async Task<EnvironmentMap> EnvironmentsAsync(
        Project project, Bundle bundle, Taken taken, List<string> skipped,
        CancellationToken cancellation)
    {
        var map = new EnvironmentMap();

        // The ones already here are still mapped, so a scenario that names one lands on it rather
        // than losing its environment for having arrived second.
        foreach (var existing in await db.Environments
                     .Where(environment => environment.ProjectId == project.Id)
                     .ToListAsync(cancellation))
        {
            map.BySlug[existing.Slug] = existing.Id;
        }

        foreach (var incoming in bundle.Environments)
        {
            if (taken.Environments.Contains(incoming.Slug))
            {
                skipped.Add(incoming.Slug);
                map.Existing++;
                continue;
            }

            var environment = new ProjectEnvironment
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = incoming.Name,
                Slug = incoming.Slug,
                BaseUrl = incoming.BaseUrl,
                Kind = Enum.TryParse<EnvironmentKind>(incoming.Kind, out var kind)
                    ? kind
                    : EnvironmentKind.Custom,
                IsProduction = incoming.IsProduction,
                TimeoutSeconds = incoming.TimeoutSeconds > 0 ? incoming.TimeoutSeconds : 30,
                MaxRedirects = incoming.MaxRedirects,
                MaxResponseKilobytes = incoming.MaxResponseKilobytes > 0
                    ? incoming.MaxResponseKilobytes
                    : 4096,
                AllowedHosts = incoming.AllowedHosts,
                AllowPrivateNetwork = incoming.AllowPrivateNetwork,
                AllowInvalidCertificate = incoming.AllowInvalidCertificate,
                DefaultHeadersJson = incoming.DefaultHeadersJson,
                SortOrder = incoming.SortOrder,
            };

            db.Environments.Add(environment);
            map.BySlug[incoming.Slug] = environment.Id;
            map.Added++;

            foreach (var variable in incoming.Variables)
            {
                db.Variables.Add(new EnvironmentVariable
                {
                    WorkspaceId = project.WorkspaceId,
                    ProjectId = project.Id,
                    EnvironmentId = environment.Id,
                    Name = variable.Name,
                    Value = variable.Value,
                    Description = variable.Description,
                });
            }
        }

        return map;
    }

    private async Task<int> ScenariosAsync(
        Project project, Bundle bundle, Taken taken, EnvironmentMap environments,
        List<string> skipped, CancellationToken cancellation)
    {
        var added = 0;

        foreach (var incoming in bundle.Scenarios)
        {
            if (taken.Scenarios.Contains(incoming.Slug))
            {
                skipped.Add(incoming.Slug);
                continue;
            }

            var scenario = new TestScenario
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = incoming.Name,
                Description = incoming.Description,
                EnvironmentId = incoming.Environment is { } slug
                    ? environments.BySlug.GetValueOrDefault(slug)
                    : null,
                CreatedByUserId = me.UserId ?? Guid.Empty,
            };

            db.Scenarios.Add(scenario);
            await db.SaveChangesAsync(cancellation);

            // Through the same service the canvas uses, which is what makes an imported scenario a
            // real scenario: it validates, it numbers the version, and it records whether the graph
            // can run. A second write path here would be a second set of rules to keep in step.
            await graphs.SaveAsync(scenario, incoming.Graph, cancellation);

            added++;
        }

        return added;
    }

    private int BaselinesFrom(
        Project project, Bundle bundle, Taken taken, EnvironmentMap environments,
        List<string> skipped)
    {
        var added = 0;

        foreach (var incoming in bundle.Baselines)
        {
            if (taken.Baselines.Contains(incoming.Slug))
            {
                skipped.Add(incoming.Slug);
                continue;
            }

            var baseline = new Baseline
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = incoming.Name,
                Description = incoming.Description,
                EnvironmentId = incoming.Environment is { } slug
                    ? environments.BySlug.GetValueOrDefault(slug)
                    : null,
                RequestJson = incoming.RequestJson,
                CreatedByUserId = me.UserId ?? Guid.Empty,
            };

            db.Baselines.Add(baseline);
            added++;

            if (incoming.Approved is not { } approved) continue;

            // Approved on arrival, and that is a real decision rather than an oversight. The team
            // that exported it approved it; re-deciding on the far side would mean every import
            // lands a pile of work in somebody's inbox for changes nobody made.
            var version = new BaselineVersion
            {
                WorkspaceId = project.WorkspaceId,
                BaselineId = baseline.Id,
                Number = 1,
                Status = BaselineStatus.Approved,
                Body = approved.Body,
                ContentType = approved.ContentType,
                StatusCode = approved.StatusCode,
                HeadersJson = approved.HeadersJson,
                RulesJson = approved.RulesJson,
                Description = approved.Description,
                CreatedByUserId = me.UserId ?? Guid.Empty,
                ApprovedByUserId = me.UserId,
                ApprovedAt = clock.UtcNow,
            };

            db.BaselineVersions.Add(version);
            baseline.ApprovedVersionId = version.Id;
        }

        return added;
    }

    private int DataSetsFrom(Project project, Bundle bundle, Taken taken, List<string> skipped)
    {
        var added = 0;

        foreach (var incoming in bundle.DataSets)
        {
            if (taken.DataSets.Contains(incoming.Slug))
            {
                skipped.Add(incoming.Slug);
                continue;
            }

            var set = new DataSet
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = incoming.Name,
                Description = incoming.Description,
                KeyColumn = incoming.KeyColumn,
                CreatedByUserId = me.UserId ?? Guid.Empty,
            };

            db.DataSets.Add(set);

            var version = new DataSetVersion
            {
                WorkspaceId = project.WorkspaceId,
                DataSetId = set.Id,
                Number = 1,
                ColumnsJson = incoming.ColumnsJson,
                RowCount = incoming.Rows.Count,
                CreatedByUserId = me.UserId ?? Guid.Empty,
            };

            db.DataSetVersions.Add(version);
            set.CurrentVersionId = version.Id;

            var ordinal = 0;

            foreach (var row in incoming.Rows)
            {
                db.DataSetRows.Add(new DataSetRow
                {
                    WorkspaceId = project.WorkspaceId,
                    DataSetVersionId = version.Id,
                    Ordinal = ordinal++,
                    Key = row.Key,
                    ValuesJson = row.ValuesJson,
                    Enabled = row.Enabled,
                });
            }

            added++;
        }

        return added;
    }

    private async Task<int> SchedulesAsync(
        Project project, Bundle bundle, Taken taken, EnvironmentMap environments,
        List<string> skipped, CancellationToken cancellation)
    {
        if (bundle.Schedules.Count == 0) return 0;

        var scenarios = (await db.Scenarios
                .Where(scenario => scenario.ProjectId == project.Id)
                .Select(scenario => new { scenario.Id, scenario.Name })
                .ToListAsync(cancellation))
            .GroupBy(scenario => Slug.From(scenario.Name, "scenario"))
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);

        var added = 0;

        foreach (var incoming in bundle.Schedules)
        {
            var slug = Slug.From(incoming.Name, "schedule");

            if (taken.Schedules.Contains(slug))
            {
                skipped.Add(slug);
                continue;
            }

            var schedule = new RunSchedule
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Name = incoming.Name,
                Cron = incoming.Cron,
                TimeZoneId = string.IsNullOrWhiteSpace(incoming.TimeZone) ? "UTC" : incoming.TimeZone,

                // Off on arrival, whatever the file says. An import that switched on a schedule
                // pointing at somebody's production API is a request nobody made, at a time nobody
                // chose. Switching it on is one click and a decision.
                Enabled = false,
            };

            db.RunSchedules.Add(schedule);

            foreach (var scenario in incoming.Scenarios)
            {
                if (!scenarios.TryGetValue(scenario, out var scenarioId)) continue;

                db.ScheduleScenarios.Add(new ScheduleScenario
                {
                    WorkspaceId = project.WorkspaceId,
                    RunScheduleId = schedule.Id,
                    ScenarioId = scenarioId,
                });
            }

            foreach (var environment in incoming.Environments)
            {
                if (!environments.BySlug.TryGetValue(environment, out var environmentId)) continue;

                db.ScheduleEnvironments.Add(new ScheduleEnvironment
                {
                    WorkspaceId = project.WorkspaceId,
                    RunScheduleId = schedule.Id,
                    EnvironmentId = environmentId,
                });
            }

            added++;
        }

        return added;
    }

    /// <summary>The slugs already in use, read once so the preview and the apply agree.</summary>
    private async Task<Taken> TakenAsync(Guid projectId, CancellationToken cancellation)
    {
        var scenarios = await db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId)
            .Select(scenario => scenario.Name)
            .ToListAsync(cancellation);

        var baselines = await db.Baselines
            .Where(baseline => baseline.ProjectId == projectId)
            .Select(baseline => baseline.Name)
            .ToListAsync(cancellation);

        var dataSets = await db.DataSets
            .Where(set => set.ProjectId == projectId)
            .Select(set => set.Name)
            .ToListAsync(cancellation);

        var schedules = await db.RunSchedules
            .Where(schedule => schedule.ProjectId == projectId)
            .Select(schedule => schedule.Name)
            .ToListAsync(cancellation);

        return new Taken
        {
            Environments =
            [
                .. await db.Environments
                    .Where(environment => environment.ProjectId == projectId)
                    .Select(environment => environment.Slug)
                    .ToListAsync(cancellation),
            ],
            Scenarios = [.. scenarios.Select(name => Slug.From(name, "scenario"))],
            Baselines = [.. baselines.Select(name => Slug.From(name, "baseline"))],
            DataSets = [.. dataSets.Select(name => Slug.From(name, "dataset"))],
            Schedules = [.. schedules.Select(name => Slug.From(name, "schedule"))],
        };
    }

    private sealed class Taken
    {
        public HashSet<string> Environments { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Scenarios { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Baselines { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> DataSets { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Schedules { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class EnvironmentMap
    {
        public Dictionary<string, Guid> BySlug { get; } = new(StringComparer.Ordinal);
        public int Added { get; set; }
        public int Existing { get; set; }
    }
}
