using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Runs;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// The same tests, at the same moment, in more than one place.
///
/// This is the question the product is for. A scenario that passes in staging and fails in
/// production is a fact about the difference between them, and the only way to see it is to run
/// both and put the answers next to each other — which is what a matrix is.
///
/// It queues rather than runs. N scenarios across M environments is N×M runs, each of which can
/// take minutes, and the browser is sent to a grid that fills in as they finish.
/// </summary>
public sealed class MatrixService(
    ProofFlowDbContext db,
    RunService runs,
    IRunQueue queue,
    ICurrentUser me,
    IClock clock)
{
    /// <summary>
    /// The most cells one press may start.
    ///
    /// Not a guess at what anybody needs — a ceiling on what a mis-click costs. Every cell is a
    /// real run against somebody's real API, and twelve scenarios across eight environments is
    /// ninety-six of them.
    /// </summary>
    public const int MaxCells = 60;

    /// <summary>
    /// Starts a batch and returns it with every cell queued.
    ///
    /// The order matters: all the runs are written before any is queued, so the grid the browser
    /// opens is complete from the first render. A grid that grows a column while somebody is
    /// reading it is a grid they lose their place in.
    /// </summary>
    public async Task<RunBatch> QueueAsync(
        Guid projectId, IReadOnlyList<Guid> scenarioIds, IReadOnlyList<Guid> environmentIds,
        string? name = null, IReadOnlyDictionary<string, string?>? inputs = null,
        CancellationToken cancellation = default)
    {
        if (scenarioIds.Count == 0) throw new InvalidOperationException("No scenario was chosen.");
        if (environmentIds.Count == 0) throw new InvalidOperationException("No environment was chosen.");

        var cells = scenarioIds.Count * environmentIds.Count;

        if (cells > MaxCells)
        {
            throw new InvalidOperationException(
                $"That is {cells} runs against real APIs. {MaxCells} is the most one press starts.");
        }

        var project = await db.Projects
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellation)
            ?? throw new InvalidOperationException("No such project in this workspace.");

        // Checked here rather than trusted from the form: a browser can be made to say anything,
        // and a scenario id from another project would run somebody else's test.
        var scenarios = await db.Scenarios
            .Where(scenario => scenario.ProjectId == projectId && scenarioIds.Contains(scenario.Id))
            .Select(scenario => scenario.Id)
            .ToListAsync(cancellation);

        var environments = await db.Environments
            .Where(environment => environment.ProjectId == projectId
                                  && environmentIds.Contains(environment.Id))
            .OrderBy(environment => environment.SortOrder)
            .Select(environment => environment.Id)
            .ToListAsync(cancellation);

        if (scenarios.Count == 0) throw new InvalidOperationException("No scenario was chosen.");
        if (environments.Count == 0) throw new InvalidOperationException("No environment was chosen.");

        var batch = new RunBatch
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Trigger = RunTrigger.Person,
            StartedByUserId = me.UserId,
            Total = scenarios.Count * environments.Count,
        };

        db.RunBatches.Add(batch);
        await db.SaveChangesAsync(cancellation);

        var queued = new List<TestRun>();

        // Scenario-major: the rows of the grid fill left to right, which is how somebody watching
        // reads them.
        foreach (var scenarioId in scenarios)
        {
            foreach (var environmentId in environments)
            {
                // The same inputs to every cell. Four scenarios across two environments reading
                // {{inputs.orderId}} is one order checked eight ways, which is what a release
                // check is — not eight different orders.
                var run = await runs.QueueAsync(
                    scenarioId, environmentId, RunTrigger.Person,
                    inputs: inputs, cancellation: cancellation);

                run.BatchId = batch.Id;
                queued.Add(run);
            }
        }

        await db.SaveChangesAsync(cancellation);

        foreach (var run in queued)
        {
            await queue.EnqueueAsync(new QueuedRun(run.Id, run.WorkspaceId), cancellation);
        }

        return batch;
    }

    /// <summary>
    /// The grid as it stands: a row per scenario, a column per environment, a run in each cell.
    ///
    /// One query for the runs and two small ones for the names, rather than a join per cell. A
    /// matrix is read repeatedly while it fills, and a read that costs sixty round trips is a read
    /// somebody stops doing.
    /// </summary>
    public async Task<MatrixDto?> ReadAsync(Guid batchId, CancellationToken cancellation = default)
    {
        var batch = await db.RunBatches
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId, cancellation);

        if (batch is null) return null;

        var cells = await db.Runs
            .Where(run => run.BatchId == batchId)
            .Select(run => new
            {
                run.Id,
                run.ScenarioId,
                run.EnvironmentId,
                run.Status,
                run.DurationMs,
                run.AssertionsPassed,
                run.AssertionsFailed,
                run.Outcome,
            })
            .ToListAsync(cancellation);

        var scenarioIds = cells.Select(cell => cell.ScenarioId).Distinct().ToList();
        var environmentIds = cells.Select(cell => cell.EnvironmentId).Distinct().ToList();

        var scenarioNames = await db.Scenarios
            .Where(scenario => scenarioIds.Contains(scenario.Id))
            .ToDictionaryAsync(scenario => scenario.Id, scenario => scenario.Name, cancellation);

        var environments = await db.Environments
            .Where(environment => environmentIds.Contains(environment.Id))
            .OrderBy(environment => environment.SortOrder)
            .Select(environment => new MatrixColumnDto
            {
                EnvironmentId = environment.Id,
                Name = environment.Name,
                IsProduction = environment.IsProduction,
            })
            .ToListAsync(cancellation);

        var byRun = cells.ToDictionary(cell => (cell.ScenarioId, cell.EnvironmentId));

        var rows = scenarioIds
            .OrderBy(id => scenarioNames.GetValueOrDefault(id) ?? string.Empty, StringComparer.Ordinal)
            .Select(scenarioId => new MatrixRowDto
            {
                ScenarioId = scenarioId,
                Name = scenarioNames.GetValueOrDefault(scenarioId) ?? string.Empty,
                Cells = [.. environments.Select(column =>
                    byRun.TryGetValue((scenarioId, column.EnvironmentId), out var cell)
                        ? new MatrixCellDto
                        {
                            RunId = cell.Id,
                            Status = cell.Status.ToString(),
                            DurationMs = cell.DurationMs,
                            AssertionsPassed = cell.AssertionsPassed,
                            AssertionsFailed = cell.AssertionsFailed,
                            Outcome = cell.Outcome,
                        }
                        // A hole rather than a fabricated pass. A cell with no run behind it is a
                        // combination nobody asked for, and drawing it as anything else would be a
                        // claim about a test that never ran.
                        : null)],
            })
            .ToList();

        var finished = cells.Count > 0
                       && cells.All(cell => cell.Status is not (RunStatus.Queued or RunStatus.Running));

        var state = !finished
            ? cells.Any(cell => cell.Status != RunStatus.Queued) ? BatchState.Running : BatchState.Queued
            : cells.All(cell => cell.Status == RunStatus.Passed) ? BatchState.Passed : BatchState.Failed;

        // Written once, when the last cell lands. The finished time is read on every list page and
        // recomputing it from sixty runs each time would be sixty rows to answer one question.
        if (finished && batch.FinishedAt is null)
        {
            batch.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellation);
        }

        return new MatrixDto
        {
            BatchId = batch.Id,
            Name = batch.Name,
            State = state.ToString(),
            Total = batch.Total,
            Done = cells.Count(cell => cell.Status is not (RunStatus.Queued or RunStatus.Running)),
            StartedAt = batch.CreatedAt,
            FinishedAt = batch.FinishedAt,
            Columns = environments,
            Rows = rows,
        };
    }
}
