using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Scenarios;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class TestSuiteConfiguration : IEntityTypeConfiguration<TestSuite>
{
    public void Configure(EntityTypeBuilder<TestSuite> builder)
    {
        builder.ToTable("TestSuites");
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);

        builder.HasIndex(s => new { s.WorkspaceId, s.ProjectId, s.Name }).IsUnique();
    }
}

public sealed class TestScenarioConfiguration : IEntityTypeConfiguration<TestScenario>
{
    public void Configure(EntityTypeBuilder<TestScenario> builder)
    {
        builder.ToTable("TestScenarios");
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);

        builder.HasIndex(s => new { s.WorkspaceId, s.ProjectId, s.Name }).IsUnique();
        builder.HasIndex(s => new { s.ProjectId, s.TestSuiteId });

        builder.HasMany(s => s.Versions)
            .WithOne(v => v.Scenario!)
            .HasForeignKey(v => v.ScenarioId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Suite)
            .WithMany()
            .HasForeignKey(s => s.TestSuiteId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ScenarioVersionConfiguration : IEntityTypeConfiguration<ScenarioVersion>
{
    public void Configure(EntityTypeBuilder<ScenarioVersion> builder)
    {
        builder.ToTable("ScenarioVersions");
        builder.Property(v => v.Description).HasMaxLength(2000);

        builder.HasIndex(v => new { v.ScenarioId, v.Number }).IsUnique();
        builder.HasIndex(v => new { v.ScenarioId, v.Status });

        builder.HasMany(v => v.Nodes)
            .WithOne(n => n.Version!)
            .HasForeignKey(n => n.ScenarioVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Connections)
            .WithOne(c => c.Version!)
            .HasForeignKey(c => c.ScenarioVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkflowNodeConfiguration : IEntityTypeConfiguration<WorkflowNode>
{
    public void Configure(EntityTypeBuilder<WorkflowNode> builder)
    {
        builder.ToTable("WorkflowNodes");
        builder.Property(n => n.Key).HasMaxLength(80).IsRequired();
        builder.Property(n => n.Name).HasMaxLength(200).IsRequired();
        builder.Property(n => n.Note).HasMaxLength(2000);

        // The name is what {{steps.name.…}} refers to, so two the same inside one version would
        // make every such reference ambiguous — and only at run time, which is the worst moment.
        builder.HasIndex(n => new { n.ScenarioVersionId, n.Name }).IsUnique();
        builder.HasIndex(n => new { n.ScenarioVersionId, n.ParentNodeId });

        // No cascade on the parent: deleting a container should be a decision about its contents,
        // taken in the editor where they are visible, not a silent sweep in the database.
        builder.HasOne<WorkflowNode>()
            .WithMany()
            .HasForeignKey(n => n.ParentNodeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WorkflowConnectionConfiguration : IEntityTypeConfiguration<WorkflowConnection>
{
    public void Configure(EntityTypeBuilder<WorkflowConnection> builder)
    {
        builder.ToTable("WorkflowConnections");
        builder.Property(c => c.FromPort).HasMaxLength(60).IsRequired();
        builder.Property(c => c.ToPort).HasMaxLength(60).IsRequired();
        builder.Property(c => c.Label).HasMaxLength(200);

        // One edge per (source port, target port) pair. Two identical edges are invisible on the
        // canvas and would make the runner take the same branch twice.
        builder.HasIndex(c => new { c.ScenarioVersionId, c.FromNodeId, c.FromPort, c.ToNodeId, c.ToPort })
            .IsUnique();

        builder.HasIndex(c => new { c.ScenarioVersionId, c.ToNodeId });

        // Restrict rather than cascade: an edge is deleted with its version, and removing a node
        // has to remove its edges explicitly so the count is reportable.
        builder.HasOne<WorkflowNode>()
            .WithMany()
            .HasForeignKey(c => c.FromNodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowNode>()
            .WithMany()
            .HasForeignKey(c => c.ToNodeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
