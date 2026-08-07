using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Runs;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class TestRunConfiguration : IEntityTypeConfiguration<TestRun>
{
    public void Configure(EntityTypeBuilder<TestRun> builder)
    {
        builder.ToTable("TestRuns");
        builder.Property(r => r.Outcome).HasMaxLength(2000);

        // The list every project page opens with: this project's runs, newest first.
        builder.HasIndex(r => new { r.ProjectId, r.CreatedAt });
        builder.HasIndex(r => new { r.ScenarioId, r.CreatedAt });
        builder.HasIndex(r => new { r.WorkspaceId, r.Status });

        builder.HasMany(r => r.Nodes)
            .WithOne(n => n.Run!)
            .HasForeignKey(n => n.TestRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Events)
            .WithOne(e => e.Run!)
            .HasForeignKey(e => e.TestRunId)
            .OnDelete(DeleteBehavior.Cascade);

        // A run outlives the version it ran and the data set it ran over — it holds a snapshot, and
        // deleting the original must not erase the record of what happened.
        builder.HasOne(r => r.Scenario)
            .WithMany()
            .HasForeignKey(r => r.ScenarioId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NodeRunConfiguration : IEntityTypeConfiguration<NodeRun>
{
    public void Configure(EntityTypeBuilder<NodeRun> builder)
    {
        builder.ToTable("NodeRuns");
        builder.Property(n => n.NodeId).HasMaxLength(64).IsRequired();
        builder.Property(n => n.NodeKey).HasMaxLength(80).IsRequired();
        builder.Property(n => n.NodeName).HasMaxLength(200).IsRequired();
        builder.Property(n => n.TakenPort).HasMaxLength(60);
        builder.Property(n => n.FailureMessage).HasMaxLength(2000);

        builder.HasIndex(n => new { n.TestRunId, n.SortOrder });
        builder.HasIndex(n => new { n.TestRunId, n.NodeId });

        builder.HasMany(n => n.Assertions)
            .WithOne(a => a.NodeRun!)
            .HasForeignKey(a => a.NodeRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AssertionResultConfiguration : IEntityTypeConfiguration<AssertionResult>
{
    public void Configure(EntityTypeBuilder<AssertionResult> builder)
    {
        builder.ToTable("AssertionResults");
        builder.Property(a => a.Description).HasMaxLength(500).IsRequired();
        builder.Property(a => a.Target).HasMaxLength(500);
        builder.Property(a => a.Expected).HasMaxLength(4000);
        builder.Property(a => a.Actual).HasMaxLength(4000);

        builder.HasIndex(a => new { a.NodeRunId, a.Passed });
    }
}

public sealed class RunEventConfiguration : IEntityTypeConfiguration<RunEvent>
{
    public void Configure(EntityTypeBuilder<RunEvent> builder)
    {
        builder.ToTable("RunEvents");
        builder.Property(e => e.Message).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.NodeId).HasMaxLength(64);
        builder.Property(e => e.NodeName).HasMaxLength(200);

        // The console reads forward from wherever it left off, so the sequence is the index — and
        // it is unique because two lines sharing a number would make "resume after 400" ambiguous.
        builder.HasIndex(e => new { e.TestRunId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.TestRunId, e.Level });
    }
}

public sealed class RunArtifactConfiguration : IEntityTypeConfiguration<RunArtifact>
{
    public void Configure(EntityTypeBuilder<RunArtifact> builder)
    {
        builder.ToTable("RunArtifacts");
        builder.Property(a => a.Name).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Kind).HasMaxLength(40).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(200);

        builder.HasIndex(a => new { a.TestRunId, a.NodeRunId });
    }
}
