using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.Domain.Data;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class DataSetConfiguration : IEntityTypeConfiguration<DataSet>
{
    public void Configure(EntityTypeBuilder<DataSet> builder)
    {
        builder.ToTable("DataSets");
        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(2000);
        builder.Property(d => d.KeyColumn).HasMaxLength(200);

        builder.HasIndex(d => new { d.WorkspaceId, d.ProjectId, d.Name }).IsUnique();

        builder.HasMany(d => d.Versions)
            .WithOne(v => v.DataSet!)
            .HasForeignKey(v => v.DataSetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DataSetVersionConfiguration : IEntityTypeConfiguration<DataSetVersion>
{
    public void Configure(EntityTypeBuilder<DataSetVersion> builder)
    {
        builder.ToTable("DataSetVersions");
        builder.Property(v => v.Description).HasMaxLength(2000);

        builder.HasIndex(v => new { v.DataSetId, v.Number }).IsUnique();

        builder.HasMany(v => v.Rows)
            .WithOne(r => r.Version!)
            .HasForeignKey(r => r.DataSetVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DataSetRowConfiguration : IEntityTypeConfiguration<DataSetRow>
{
    public void Configure(EntityTypeBuilder<DataSetRow> builder)
    {
        builder.ToTable("DataSetRows");
        builder.Property(r => r.Key).HasMaxLength(400).IsRequired();

        // Ordinal, not key: a set is read in order two thousand times per run, and a key is only
        // ever looked up one at a time.
        builder.HasIndex(r => new { r.DataSetVersionId, r.Ordinal }).IsUnique();
        builder.HasIndex(r => new { r.DataSetVersionId, r.Key });
    }
}

public sealed class CaptureSessionConfiguration : IEntityTypeConfiguration<CaptureSession>
{
    public void Configure(EntityTypeBuilder<CaptureSession> builder)
    {
        builder.ToTable("CaptureSessions");
        builder.Property(s => s.StoppedReason).HasMaxLength(1000);

        builder.HasIndex(s => new { s.ProjectId, s.StartedAt });
        builder.HasIndex(s => new { s.BaselineId, s.Status });

        builder.HasMany(s => s.Samples)
            .WithOne(sample => sample.Session!)
            .HasForeignKey(sample => sample.CaptureSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // The data-set version a session ran against is never deleted out from under it: a session
        // that cannot say what it ran against is a session nobody can argue with.
        builder.HasOne(s => s.DataSetVersion)
            .WithMany()
            .HasForeignKey(s => s.DataSetVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CaptureSampleConfiguration : IEntityTypeConfiguration<CaptureSample>
{
    public void Configure(EntityTypeBuilder<CaptureSample> builder)
    {
        builder.ToTable("CaptureSamples");
        builder.Property(s => s.Key).HasMaxLength(400).IsRequired();
        builder.Property(s => s.ContentType).HasMaxLength(200);
        builder.Property(s => s.NormalizedHash).HasMaxLength(64);
        builder.Property(s => s.FailureMessage).HasMaxLength(2000);
        builder.Property(s => s.ReviewNote).HasMaxLength(2000);
        builder.Property(s => s.ResolvedUrl).HasMaxLength(4000);

        builder.HasIndex(s => new { s.CaptureSessionId, s.Ordinal }).IsUnique();

        // The review queue's default order: what differs, first. Without this index it is a sort
        // over every sample in the session, which is the two-thousand-row case.
        builder.HasIndex(s => new { s.CaptureSessionId, s.Status, s.Differs });
        builder.HasIndex(s => new { s.CaptureSessionId, s.Key });
    }
}

public sealed class BaselineSampleConfiguration : IEntityTypeConfiguration<BaselineSample>
{
    public void Configure(EntityTypeBuilder<BaselineSample> builder)
    {
        builder.ToTable("BaselineSamples");
        builder.Property(s => s.Key).HasMaxLength(400).IsRequired();
        builder.Property(s => s.ContentType).HasMaxLength(200);
        builder.Property(s => s.NormalizedHash).HasMaxLength(64);

        // One approved answer per input. Two would mean a regression run had to choose.
        builder.HasIndex(s => new { s.BaselineId, s.Key }).IsUnique();

        builder.HasOne(s => s.Baseline)
            .WithMany()
            .HasForeignKey(s => s.BaselineId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
