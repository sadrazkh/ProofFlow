using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Contracts.Runners;
using ProofFlow.Domain.Environments;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Runners;
using ProofFlow.Domain.Runs;
using ProofFlow.Domain.Scenarios;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runners;
using ProofFlow.Infrastructure.Tenancy;
using ProofFlow.Web.Controllers;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// The whole conversation an agent has, over real HTTP.
///
/// Enrol, ask for work, verify the signature, report. Every step through the actual endpoints,
/// because the interesting failures here are not in the services — those have their own tests —
/// but in the seam: which workspace a token puts a request in, what an unauthenticated call gets,
/// and whether a signature computed on this side matches one computed on the other.
/// </summary>
public sealed class RunnerApiTests(ProofFlowApplication app) : IClassFixture<ProofFlowApplication>
{
    [Fact]
    public async Task An_agent_enrols_claims_a_signed_job_and_reports_on_it()
    {
        var (workspaceId, runnerId, runId) = await SeedAsync();

        var client = app.CreateClient();

        // ---- enrol
        var code = await CodeAsync(workspaceId, runnerId);

        var enrolled = await (await client.PostAsJsonAsync("/api/v1/runners/enroll", new EnrollRequest
        {
            Code = code,
            Hostname = "build-01.internal",
            Version = "1.0.0",
        })).Content.ReadFromJsonAsync<EnrollResponse>();

        enrolled.Should().NotBeNull();
        enrolled!.RunnerId.Should().Be(runnerId);
        enrolled.Token.Should().NotBeNullOrWhiteSpace();

        // ---- claim
        client.DefaultRequestHeaders.Add(RunnerApiController.TokenHeader, enrolled.Token);

        var claimed = await client.PostAsync("/api/v1/runners/jobs/claim", null);

        claimed.StatusCode.Should().Be(HttpStatusCode.OK);

        var job = await claimed.Content.ReadFromJsonAsync<SignedJob>();

        job.Should().NotBeNull();
        job!.JobId.Should().Be(runId);

        // ---- verify, exactly as an agent would
        JobSignature.Verify(job.Payload, job.Signature, enrolled.SigningKey)
            .Should().BeTrue("an agent has to be able to check the work came from this installation");

        // And the environment it cannot otherwise reach travelled with it.
        using var payload = JsonDocument.Parse(job.Payload);

        payload.RootElement.GetProperty("environment").GetProperty("baseUrl")
            .GetString().Should().Be("https://inside.example.internal");

        payload.RootElement.GetProperty("definition").GetString()
            .Should().NotBeNullOrWhiteSpace("the graph travels as a snapshot, not as a reference");

        // ---- report
        var reported = await client.PostAsJsonAsync("/api/v1/runners/jobs/result", new JobResult
        {
            JobId = runId,
            Status = "Passed",
            Outcome = "Everything that was checked held.",
            Steps = 3,
            AssertionsPassed = 1,
            DurationMs = 128,
        });

        reported.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await ReadRunAsync(workspaceId, runId);

        run.Status.Should().Be(RunStatus.Passed);
        run.StepsRun.Should().Be(3);
        run.DurationMs.Should().Be(128);
        run.Outcome.Should().Be("Everything that was checked held.");
        run.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_claimed_job_is_not_handed_out_twice()
    {
        // Two agents enrolled as the same runner must not both run the same scenario against the
        // same environment.
        var (workspaceId, runnerId, _) = await SeedAsync();

        var client = app.CreateClient();
        var enrolled = await EnrolAsync(client, workspaceId, runnerId);

        client.DefaultRequestHeaders.Add(RunnerApiController.TokenHeader, enrolled.Token);

        (await client.PostAsync("/api/v1/runners/jobs/claim", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/v1/runners/jobs/claim", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Without_a_token_there_is_nothing_to_ask_for()
    {
        var client = app.CreateClient();

        (await client.PostAsync("/api/v1/runners/jobs/claim", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/api/v1/runners/jobs/result", new JobResult
        {
            JobId = Guid.CreateVersion7(),
            Status = "Passed",
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_code_that_is_not_usable_gets_one_answer_whatever_is_wrong_with_it()
    {
        // "No such code", "already used" and "expired" are the same reply. Three replies would let
        // somebody with a list of guesses learn which ones were once real.
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/runners/enroll", new EnrollRequest
        {
            Code = "ZZZZ-ZZZZ-ZZZZ-ZZZZ",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_agent_cannot_report_on_a_run_that_is_not_its_own()
    {
        var (_, _, otherRunId) = await SeedAsync();
        var (workspaceId, runnerId, _) = await SeedAsync();

        var client = app.CreateClient();
        var enrolled = await EnrolAsync(client, workspaceId, runnerId);

        client.DefaultRequestHeaders.Add(RunnerApiController.TokenHeader, enrolled.Token);

        var response = await client.PostAsJsonAsync("/api/v1/runners/jobs/result", new JobResult
        {
            JobId = otherRunId,
            Status = "Passed",
        });

        // A token is a credential for one runner, not for the workspace. An agent that could report
        // on somebody else's run could mark a failing test green from behind a firewall.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_run_bound_to_a_runner_is_left_alone_by_the_local_worker()
    {
        // The whole arrangement rests on this. The server cannot reach the environment — that is
        // why the agent exists — so a local execution would fail for a reason nobody can see.
        var (workspaceId, _, runId) = await SeedAsync();

        using var scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(workspaceId);

        await scope.ServiceProvider
            .GetRequiredService<ProofFlow.Infrastructure.Runs.RunService>()
            .ExecuteAsync(runId);

        (await ReadRunAsync(workspaceId, runId)).Status.Should().Be(RunStatus.Queued);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private async Task<EnrollResponse> EnrolAsync(HttpClient client, Guid workspaceId, Guid runnerId)
    {
        var code = await CodeAsync(workspaceId, runnerId);

        var response = await client.PostAsJsonAsync("/api/v1/runners/enroll",
            new EnrollRequest { Code = code });

        return (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;
    }

    /// <summary>
    /// Issues a code for a runner that already exists.
    ///
    /// Through the service, because the code is returned exactly once and there is no way to read
    /// one back — which is the property being relied on rather than worked around.
    /// </summary>
    private async Task<string> CodeAsync(Guid workspaceId, Guid runnerId)
    {
        using var scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(workspaceId);

        return (await scope.ServiceProvider.GetRequiredService<RunnerService>()
            .ReissueAsync(workspaceId, runnerId))!;
    }

    private async Task<TestRun> ReadRunAsync(Guid workspaceId, Guid runId)
    {
        using var scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>()
            .Runs.AsNoTracking().FirstAsync(run => run.Id == runId);
    }

    /// <summary>
    /// A workspace with an unreachable environment, a runner for it, and one run waiting.
    ///
    /// Written straight to the database rather than through the interface: this test is about the
    /// agent's side of the conversation, and building a project through the UI first would make it
    /// a test of the UI that happens to end with an agent.
    /// </summary>
    private async Task<(Guid Workspace, Guid Runner, Guid Run)> SeedAsync()
    {
        var workspaceId = Guid.CreateVersion7();

        using var scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<BackgroundWorkspace>().ActFor(workspaceId);

        var db = scope.ServiceProvider.GetRequiredService<ProofFlowDbContext>();

        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Behind a firewall",
            Slug = $"w-{workspaceId:N}"[..20],
        });

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = "Internal API",
            Slug = $"p-{Guid.CreateVersion7():N}"[..20],
        };

        db.Projects.Add(project);

        var runner = new Runner
        {
            WorkspaceId = workspaceId,
            Name = "The one inside",
        };

        db.Runners.Add(runner);
        await db.SaveChangesAsync();

        var environment = new ProjectEnvironment
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Inside",
            Slug = "inside",
            BaseUrl = "https://inside.example.internal",
            RunnerId = runner.Id,
        };

        var scenario = new TestScenario
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Name = "Read one record",
        };

        db.Environments.Add(environment);
        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync();

        var run = new TestRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            ScenarioId = scenario.Id,
            EnvironmentId = environment.Id,
            RunnerId = runner.Id,
            Status = RunStatus.Queued,
            Trigger = RunTrigger.Person,
            DefinitionJson = """{"nodes":[],"edges":[]}""",
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync();

        return (workspaceId, runner.Id, run.Id);
    }
}
