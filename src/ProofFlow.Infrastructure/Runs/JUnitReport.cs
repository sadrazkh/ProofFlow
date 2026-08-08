using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Domain.Runs;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Runs;

/// <summary>
/// A run, in the one format every build system already reads.
///
/// JUnit XML is nobody's favourite schema and that is beside the point: Jenkins, GitLab, Azure
/// DevOps, GitHub Actions and every tool that annotates a pull request understand it, and a report
/// they understand is the difference between a test suite that fails a build and one somebody has
/// to remember to look at.
///
/// Three things are deliberate. Times are seconds with a decimal point, because that is what the
/// schema says and a reader that parses them will not accept "138ms". Timestamps are ISO Gregorian
/// in every language — the Persian interface shows Jalali, and a build server reading a Jalali date
/// would either fail or, far worse, succeed at parsing the wrong year. And a step with no
/// assertions is still a test case: a request that never completed is exactly what a build should
/// go red on, and leaving it out would make the suite look smaller and greener than it is.
/// </summary>
public sealed class JUnitReport(ProofFlowDbContext db)
{
    public async Task<XDocument?> ForRunAsync(Guid runId, CancellationToken cancellation = default)
    {
        var run = await db.Runs.FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellation);
        if (run is null) return null;

        var suite = await SuiteAsync(run, cancellation);

        return new XDocument(new XElement("testsuites",
            new XAttribute("name", "ProofFlow"),
            new XAttribute("tests", (int)suite.Attribute("tests")!),
            new XAttribute("failures", (int)suite.Attribute("failures")!),
            new XAttribute("errors", (int)suite.Attribute("errors")!),
            new XAttribute("skipped", (int)suite.Attribute("skipped")!),
            new XAttribute("time", Seconds(run.DurationMs)),
            suite));
    }

    /// <summary>
    /// A whole batch as one document: one suite per cell.
    ///
    /// Which is what a pipeline that runs across environments wants — a single artefact whose
    /// failures say which environment they came from, rather than four files nobody correlates.
    /// </summary>
    public async Task<XDocument?> ForBatchAsync(Guid batchId, CancellationToken cancellation = default)
    {
        var batch = await db.RunBatches
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId, cancellation);

        if (batch is null) return null;

        var runs = await db.Runs
            .Where(run => run.BatchId == batchId)
            .OrderBy(run => run.CreatedAt)
            .ToListAsync(cancellation);

        var suites = new List<XElement>();
        foreach (var run in runs) suites.Add(await SuiteAsync(run, cancellation));

        return new XDocument(new XElement("testsuites",
            new XAttribute("name", batch.Name ?? "ProofFlow"),
            new XAttribute("tests", suites.Sum(suite => (int)suite.Attribute("tests")!)),
            new XAttribute("failures", suites.Sum(suite => (int)suite.Attribute("failures")!)),
            new XAttribute("errors", suites.Sum(suite => (int)suite.Attribute("errors")!)),
            new XAttribute("skipped", suites.Sum(suite => (int)suite.Attribute("skipped")!)),
            new XAttribute("time", Seconds(runs.Sum(run => run.DurationMs))),
            suites));
    }

    private async Task<XElement> SuiteAsync(TestRun run, CancellationToken cancellation)
    {
        var scenario = await db.Scenarios
            .Where(candidate => candidate.Id == run.ScenarioId)
            .Select(candidate => new { candidate.Name, candidate.QuarantinedAt })
            .FirstOrDefaultAsync(cancellation);

        var environment = run.EnvironmentId is { } environmentId
            ? await db.Environments
                .Where(candidate => candidate.Id == environmentId)
                .Select(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellation)
            : null;

        var nodes = await db.NodeRuns
            .Where(node => node.TestRunId == run.Id)
            .OrderBy(node => node.SortOrder)
            .ToListAsync(cancellation);

        var assertions = await db.AssertionResults
            .Where(result => db.NodeRuns
                .Any(node => node.Id == result.NodeRunId && node.TestRunId == run.Id))
            .ToListAsync(cancellation);

        var byNode = assertions.ToLookup(result => result.NodeRunId);

        // The environment is part of the suite's name, not a property, because the failure message
        // a build annotates a pull request with is the suite name plus the case name — and "it
        // failed" without "where" sends somebody to the wrong place.
        var name = environment is null
            ? scenario?.Name ?? "scenario"
            : $"{scenario?.Name ?? "scenario"} · {environment}";

        var cases = new List<XElement>();
        var failures = 0;
        var errors = 0;
        var skipped = 0;

        // Quarantined tests report their failures as skips.
        //
        // Which is the whole meaning of quarantine: the test still runs, still records what it
        // found, and still appears in the report — it just stops being allowed to fail the build.
        // Deleting it would take its coverage away silently; hiding it would be a lie.
        var quarantined = scenario?.QuarantinedAt is not null;

        foreach (var node in nodes)
        {
            var checks = byNode[node.Id].ToList();

            if (checks.Count == 0)
            {
                // A step with nothing to check is only worth a case when it went wrong. A hundred
                // green "the request was sent" cases would bury the fifteen that mean something.
                if (node.Status is not (NodeRunStatus.Failed or NodeRunStatus.Cancelled)) continue;

                cases.Add(Case(name, node.NodeName, node.DurationMs, quarantined,
                    node.Status == NodeRunStatus.Cancelled ? "error" : "failure",
                    node.FailureMessage ?? "The step did not do what it was asked."));

                if (quarantined) skipped++;
                else if (node.Status == NodeRunStatus.Cancelled) errors++;
                else failures++;

                continue;
            }

            foreach (var check in checks)
            {
                var failed = !check.Passed;

                cases.Add(Case(name,
                    $"{node.NodeName} · {check.Description}",
                    node.DurationMs / checks.Count,
                    quarantined,
                    failed ? "failure" : null,
                    failed ? Explain(check) : null));

                if (!failed) continue;

                if (quarantined) skipped++;
                else failures++;
            }
        }

        // A run that errored before it could check anything still has to be visible, or a broken
        // scenario reads in CI as a suite with nothing in it — which is to say, as a pass.
        if (cases.Count == 0 && run.Status is RunStatus.Errored or RunStatus.Failed)
        {
            cases.Add(Case(name, "run", run.DurationMs, quarantined, "error",
                run.Outcome ?? "The run did not complete."));

            if (quarantined) skipped++;
            else errors++;
        }

        return new XElement("testsuite",
            new XAttribute("name", name),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors", errors),
            new XAttribute("skipped", skipped),
            new XAttribute("time", Seconds(run.DurationMs)),

            // Always ISO Gregorian, in every language. A Persian interface shows Jalali dates and a
            // build server reading one would either fail or parse the wrong year.
            new XAttribute("timestamp",
                (run.StartedAt ?? run.CreatedAt).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss",
                    CultureInfo.InvariantCulture)),
            cases);
    }

    private static XElement Case(
        string suite, string name, double durationMs, bool quarantined,
        string? outcome, string? message)
    {
        var element = new XElement("testcase",
            new XAttribute("classname", suite),
            new XAttribute("name", name),
            new XAttribute("time", Seconds(durationMs)));

        if (outcome is null) return element;

        if (quarantined)
        {
            element.Add(new XElement("skipped",
                new XAttribute("message", $"Quarantined. {message}")));

            return element;
        }

        element.Add(new XElement(outcome,
            new XAttribute("message", Trim(message ?? "It did not hold")),
            new XAttribute("type", outcome == "error" ? "RunError" : "AssertionFailed"),
            new XCData(Trim(message ?? string.Empty))));

        return element;
    }

    private static string Explain(AssertionResult check)
    {
        if (check.Expected is null && check.Actual is null) return check.Description;

        return $"{check.Description}\nExpected: {check.Expected ?? "—"}\nActual: {check.Actual ?? "—"}";
    }

    /// <summary>
    /// Seconds with a decimal point, invariant.
    ///
    /// The schema's unit, and the invariant culture matters more than it looks: on a machine whose
    /// locale uses a comma for the decimal separator, "0,138" is a number every JUnit reader in
    /// existence gets wrong.
    /// </summary>
    private static string Seconds(double milliseconds) =>
        (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Messages end up in a build annotation, which is not a place for four kilobytes.</summary>
    private static string Trim(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…";
}
