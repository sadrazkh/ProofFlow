using System.Reflection;
using FluentAssertions;
using ProofFlow.Domain.Common;

namespace ProofFlow.Tests;

/// <summary>
/// The layering, checked by the build rather than by discipline.
///
/// Every one of these rules is easy to break with a single convenient using directive, and none of
/// them announces itself when broken — the code compiles, the tests pass, and six months later the
/// engine cannot be run without a database.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Entity).Assembly;
    private static readonly Assembly TestEngine = typeof(TestEngine.EngineMarker).Assembly;
    private static readonly Assembly Application = typeof(Application.Abstractions.IClock).Assembly;

    [Fact]
    public void Domain_references_nothing()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        referenced.Should().NotContain(name =>
            name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("ProofFlow.", StringComparison.Ordinal));
    }

    [Fact]
    public void TestEngine_does_not_know_about_persistence_or_the_web()
    {
        var referenced = TestEngine.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        // The engine has to be runnable inside a unit test with no database and no host. The
        // moment it can name a DbContext, that stops being true and the assertion and diff logic
        // becomes testable only through an integration test.
        referenced.Should().NotContain(name =>
            name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name == "ProofFlow.Infrastructure"
            || name == "ProofFlow.Web");
    }

    [Fact]
    public void Application_holds_ports_not_implementations()
    {
        var referenced = Application.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        referenced.Should().NotContain(name =>
            name == "ProofFlow.Infrastructure" || name == "ProofFlow.Web");
    }

    [Fact]
    public void Every_workspace_owned_entity_derives_from_Entity()
    {
        // IWorkspaceOwned is what puts a row behind the tenant filter, and Entity is what stamps
        // its workspace on insert. An entity with one and not the other saves with an empty
        // workspace id and is then invisible to everyone, including the person who created it.
        var offenders = Domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IWorkspaceOwned).IsAssignableFrom(t))
            .Where(t => !typeof(Entity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();

        offenders.Should().BeEmpty();
    }
}
