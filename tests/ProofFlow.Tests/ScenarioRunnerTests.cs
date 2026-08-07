using System.Text.Json.Nodes;
using FluentAssertions;
using ProofFlow.Domain.Runs;
using ProofFlow.TestEngine.Nodes;
using ProofFlow.TestEngine.Running;

namespace ProofFlow.Tests;

/// <summary>
/// What the runner does with a shape of graph.
///
/// Written as shapes rather than as scenarios because the shape is the thing that goes wrong: a
/// loop that runs its body zero times, a branch that takes both paths, a cleanup block that gets
/// skipped exactly when it was needed. Every one of these is a mistake somebody would only find on
/// the run that mattered.
/// </summary>
public class ScenarioRunnerTests
{
    [Fact]
    public void Every_node_on_the_palette_does_something()
    {
        // The gate this whole phase turns on. A palette of seventy kinds where a dozen quietly do
        // nothing is worse than a palette of sixty: the run comes back green having tested nothing,
        // and nobody looks at a green run.
        var services = new StubServices();
        var executors = new NodeExecutors(services);

        var missing = NodeCatalogue.All
            .Select(spec => spec.Key)
            .Where(key => !executors.Handles(key) && !ScenarioRunner.Controls.Contains(key))
            .ToArray();

        missing.Should().BeEmpty(
            "every kind of node needs either an executor or a place in the runner");
    }

    [Fact]
    public async Task A_straight_line_runs_in_order_and_passes()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("first", "core.checkpoint", properties: [("name", "one")])
            .Node("second", "core.checkpoint", properties: [("name", "two")])
            .Node("end", "core.end")
            .Chain("start", "first", "second", "end")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Order.Should().Equal("start", "first", "second", "end");
    }

    [Fact]
    public async Task A_scenario_with_no_start_says_so_instead_of_doing_nothing()
    {
        var (runner, _, _) = Harness.Build();

        var graph = new GraphBuilder().Node("lonely", "core.checkpoint").Build();
        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Errored);
        summary.Outcome.Should().Contain("starting point");
    }

    [Fact]
    public async Task A_disabled_step_is_stepped_over()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("off", "http.request", disabled: true, properties: [("url", "https://example.test/")])
            .Node("on", "core.checkpoint", properties: [("name", "after")])
            .Chain("start", "off", "on")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Order.Should().Contain("on");
        sink.Finished.Should().NotContain(entry => entry.Node == "off" && entry.Status == NodeRunStatus.Passed);
    }

    // ---- branching -------------------------------------------------------------------------

    [Theory]
    [InlineData("200 == 200", "yes")]
    [InlineData("200 == 404", "no")]
    public async Task A_branch_takes_one_path_and_not_the_other(string condition, string expected)
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("branch", "flow.if", properties: [("condition", condition)])
            .Node("yes", "core.checkpoint", properties: [("name", "yes")])
            .Node("no", "core.checkpoint", properties: [("name", "no")])
            .Edge("start", "branch")
            .Edge("branch", "yes", fromPort: "true")
            .Edge("branch", "no", fromPort: "false")
            .Build();

        await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Should().Contain(expected);
        sink.Order.Should().NotContain(expected == "yes" ? "no" : "yes");
    }

    [Fact]
    public async Task A_switch_falls_through_to_default_when_nothing_matches()
    {
        var (runner, sink, _) = Harness.Build();

        var cases = """[{"name":"case1","value":"ready"},{"name":"case2","value":"pending"}]""";

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("switch", "flow.switch", properties: [("value", "gone"), ("cases", cases)])
            .Node("ready", "core.checkpoint")
            .Node("other", "core.checkpoint")
            .Edge("start", "switch")
            .Edge("switch", "ready", fromPort: "case1")
            .Edge("switch", "other", fromPort: "default")
            .Build();

        await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Should().Contain("other").And.NotContain("ready");
    }

    // ---- loops -----------------------------------------------------------------------------

    [Fact]
    public async Task A_repeat_runs_its_body_that_many_times()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("loop", "flow.repeat", properties: [("times", "3")])
            .Node("body", "core.checkpoint", parent: "loop", properties: [("name", "pass")])
            .Chain("start", "loop")
            .Build();

        await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Count(name => name == "body").Should().Be(3);
    }

    [Fact]
    public async Task A_loop_body_knows_which_pass_it_is_on()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("loop", "flow.repeat", properties: [("times", "3")])
            .Node("body", "core.log", parent: "loop", properties: [("message", "pass {{run.loop.index}}")])
            .Chain("start", "loop")
            .Build();

        await runner.RunAsync(graph, Harness.Scopes());

        sink.Logs.Select(entry => entry.Message).Should().Contain(["pass 0", "pass 1", "pass 2"]);
    }

    [Fact]
    public async Task A_while_loop_stops_at_its_ceiling_rather_than_running_for_ever()
    {
        // The required-not-optional ceiling. A while loop with no limit is a build agent that stops
        // answering, and the person who has to work out why is not the person who wrote the loop.
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("loop", "flow.while", properties: [("condition", "1 == 1"), ("maxIterations", "4")])
            .Node("body", "core.checkpoint", parent: "loop")
            .Chain("start", "loop")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Count(name => name == "body").Should().Be(4);
        summary.Status.Should().Be(RunStatus.Failed);
        summary.Outcome.Should().Contain("4 times");
    }

    [Fact]
    public async Task A_break_leaves_the_loop_and_a_continue_starts_the_next_pass()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("loop", "flow.repeat", properties: [("times", "5")])
            .Node("body", "core.checkpoint", parent: "loop")
            .Node("stop", "flow.break", parent: "loop")
            .Node("after", "core.checkpoint")
            .Edge("start", "loop")
            .Edge("body", "stop")
            .Edge("loop", "after")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Count(name => name == "body").Should().Be(1);
        sink.Order.Should().Contain("after");
        summary.Status.Should().Be(RunStatus.Passed);
    }

    [Fact]
    public async Task A_for_each_goes_through_a_list_and_publishes_each_item()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("source", "core.expression", properties: [("expression", "[10,20,30]")])
            .Node("loop", "flow.forEach")
            .Node("body", "core.log", parent: "loop", properties: [("message", "item {{run.loop.item}}")])
            .Chain("start", "source", "loop")
            .Edge("source", "loop", fromPort: "result", toPort: "list")
            .Build();

        await runner.RunAsync(graph, Harness.Scopes());

        sink.Logs.Select(entry => entry.Message)
            .Should().Contain(["item 10", "item 20", "item 30"]);
    }

    [Fact]
    public async Task A_data_set_loop_runs_once_per_row_with_the_row_in_scope()
    {
        var (runner, sink, services) = Harness.Build();

        services.Rows.Add(JsonNode.Parse("""{"id":"a-1","name":"first"}""")!);
        services.Rows.Add(JsonNode.Parse("""{"id":"a-2","name":"second"}""")!);

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("rows", "flow.forEachRow", properties: [("dataSet", "customers")])
            .Node("body", "core.log", parent: "rows",
                properties: [("message", "{{dataset.current.id}} is {{dataset.current.name}}")])
            .Chain("start", "rows")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Logs.Select(entry => entry.Message)
            .Should().Contain(["a-1 is first", "a-2 is second"]);
    }

    // ---- retry, poll, cleanup ----------------------------------------------------------------

    [Fact]
    public async Task A_retry_runs_again_after_a_failure_and_says_which_attempt_worked()
    {
        var (runner, sink, services) = Harness.Build();

        services.Then(StubServices.Response(500, "{}")).Then(StubServices.Response(200, "{}"));

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("retry", "flow.retry", properties: [("attempts", "3"), ("delay", "1ms"), ("backoff", "fixed")])
            .Node("call", "http.request", parent: "retry", properties: [("url", "https://example.test/")])
            .Node("check", "assert.status", parent: "retry", properties: [("expected", "200")])
            .Edge("start", "retry")
            .Edge("call", "check")
            .Edge("call", "check", fromPort: "response", toPort: "response")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Logs.Should().Contain(entry => entry.Message.Contains("attempt 2"));
        sink.Started.Should().Contain(entry => entry.Node == "call" && entry.Attempt == 2);
    }

    [Fact]
    public async Task A_superseded_attempt_stays_on_the_record_without_failing_the_run()
    {
        // Otherwise "it worked on the second go" reports a failed run, and the retry node becomes a
        // way of turning a flaky test into a slow failing one. The attempt is not hidden — it is in
        // the log and the timeline, which is where flaky detection reads it from — it just does not
        // get to decide.
        var (runner, sink, services) = Harness.Build();

        services.Then(StubServices.Response(500, "{}")).Then(StubServices.Response(200, "{}"));

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("retry", "flow.retry", properties: [("attempts", "2"), ("delay", "1ms"), ("backoff", "fixed")])
            .Node("call", "http.request", parent: "retry", properties: [("url", "https://example.test/")])
            .Node("check", "assert.status", parent: "retry", properties: [("expected", "200")])
            .Edge("start", "retry")
            .Edge("call", "check")
            .Edge("call", "check", fromPort: "response", toPort: "response")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        summary.AssertionsFailed.Should().Be(0, "the attempt that decided the outcome passed");
        summary.AssertionsPassed.Should().Be(1);

        sink.Assertions.Should().HaveCount(2, "both attempts are still on the record");
        sink.Assertions[0].Passed.Should().BeFalse();
    }

    [Fact]
    public async Task A_poll_stops_as_soon_as_the_condition_comes_true()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("poll", "flow.pollUntil",
                properties: [("condition", "{{run.loop.index}} == 2"), ("interval", "1ms"), ("timeout", "5s")])
            .Node("body", "core.checkpoint", parent: "poll")
            .Chain("start", "poll")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Order.Count(name => name == "body").Should().Be(3);
    }

    [Fact]
    public async Task Cleanup_runs_after_a_failure_and_in_reverse_order()
    {
        // The whole reason the node exists: a scenario that created a record has to delete it even
        // when the assertion in the middle failed. Reverse, because the last thing made is the
        // first thing that has to go.
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("firstCleanup", "flow.cleanup")
            .Node("undoFirst", "core.checkpoint", parent: "firstCleanup")
            .Node("secondCleanup", "flow.cleanup")
            .Node("undoSecond", "core.checkpoint", parent: "secondCleanup")
            .Node("boom", "core.abort", properties: [("reason", "it went wrong")])
            .Chain("start", "firstCleanup", "secondCleanup", "boom")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Failed);
        sink.Order.Should().ContainInOrder("boom", "undoSecond", "undoFirst");
    }

    [Fact]
    public async Task Cleanup_still_runs_when_the_run_was_cancelled()
    {
        var (runner, sink, _) = Harness.Build();
        using var stopping = new CancellationTokenSource();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("cleanup", "flow.cleanup")
            .Node("undo", "core.checkpoint", parent: "cleanup")
            .Node("wait", "core.delay", properties: [("duration", "30s")])
            .Chain("start", "cleanup", "wait")
            .Build();

        var run = runner.RunAsync(graph, Harness.Scopes(), stopping.Token);
        await Task.Delay(80);
        await stopping.CancelAsync();

        var summary = await run;

        summary.Status.Should().Be(RunStatus.Cancelled);
        sink.Order.Should().Contain("undo");
    }

    // ---- try, expect, parallel ---------------------------------------------------------------

    [Fact]
    public async Task A_try_block_catches_a_failure_and_the_run_carries_on()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("try", "flow.tryCatch")
            .Node("boom", "http.request", parent: "try")
            .Node("handled", "core.checkpoint")
            .Node("after", "core.checkpoint")
            .Edge("start", "try")
            .Edge("try", "after")
            .Edge("try", "handled", fromPort: "caught")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        sink.Order.Should().Contain("handled").And.NotContain("after");
        summary.Status.Should().Be(RunStatus.Passed);
    }

    [Fact]
    public async Task An_expected_failure_passes_and_does_not_count_against_the_run()
    {
        var (runner, _, services) = Harness.Build();
        services.Default = StubServices.Response(400, """{"error":"no"}""");

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("expect", "test.expectFailure", properties: [("reason", "a bad request is refused")])
            .Node("call", "http.request", parent: "expect", properties: [("url", "https://example.test/")])
            .Node("check", "assert.status", parent: "expect", properties: [("expected", "200")])
            .Edge("start", "expect")
            .Edge("call", "check")
            .Edge("call", "check", fromPort: "response", toPort: "response")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        summary.AssertionsFailed.Should().Be(0);
    }

    [Fact]
    public async Task An_expected_failure_that_passes_is_a_failure()
    {
        var (runner, _, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("expect", "test.expectFailure", properties: [("reason", "this should be refused")])
            .Node("fine", "core.checkpoint", parent: "expect")
            .Edge("start", "expect")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Failed);
        summary.Outcome.Should().Contain("did not");
    }

    [Fact]
    public async Task Parallel_branches_all_run_and_the_steps_after_the_join_run_once()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("split", "core.parallel", properties: [("maxConcurrent", "3")])
            .Node("one", "core.checkpoint")
            .Node("two", "core.checkpoint")
            .Node("join", "core.join", properties: [("wait", "all")])
            .Node("after", "core.checkpoint")
            .Edge("start", "split")
            .Edge("split", "one", fromPort: "branch1")
            .Edge("split", "two", fromPort: "branch2")
            .Edge("one", "join")
            .Edge("two", "join")
            .Edge("join", "after")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Passed);
        sink.Order.Should().Contain("one").And.Contain("two");
        sink.Order.Count(name => name == "after").Should().Be(1);
    }

    // ---- limits ------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_that_will_not_end_is_stopped_by_the_step_ceiling()
    {
        var (runner, _, _) = Harness.Build();

        // Two loops that multiply: not a mistake anybody makes on purpose, and exactly the shape
        // that takes a build agent down.
        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("outer", "flow.repeat", properties: [("times", "1000")])
            .Node("inner", "flow.repeat", parent: "outer", properties: [("times", "1000")])
            .Node("body", "core.checkpoint", parent: "inner")
            .Chain("start", "outer")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Errored);
        summary.Steps.Should().BeLessThanOrEqualTo(ScenarioRunner.MaxSteps + 10);
    }

    [Fact]
    public async Task A_step_that_throws_fails_that_step_and_not_the_process()
    {
        var (runner, sink, _) = Harness.Build();

        // No URL at all: the node cannot do its job, and the message has to name the step.
        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("broken", "http.request")
            .Node("after", "core.checkpoint")
            .Node("recover", "core.checkpoint")
            .Edge("start", "broken")
            .Edge("broken", "after")
            .Edge("broken", "recover", fromPort: "failure")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Failed);
        sink.Order.Should().Contain("recover").And.NotContain("after");
    }

    [Fact]
    public async Task An_unresolvable_reference_fails_its_own_step_and_names_itself()
    {
        var (runner, sink, _) = Harness.Build();

        var graph = new GraphBuilder()
            .Node("start", "core.start")
            .Node("call", "http.request", properties: [("url", "{{vars.missing}}/records")])
            .Chain("start", "call")
            .Build();

        var summary = await runner.RunAsync(graph, Harness.Scopes());

        summary.Status.Should().Be(RunStatus.Failed);
        sink.Finished.Should().Contain(entry =>
            entry.Node == "call" && entry.Failure != null && entry.Failure.Contains("missing"));
    }
}
