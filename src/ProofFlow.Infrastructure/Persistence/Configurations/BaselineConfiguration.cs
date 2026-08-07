using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Baselines;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class BaselineConfiguration : IEntityTypeConfiguration<Baseline>
{
    public void Configure(EntityTypeBuilder<Baseline> builder)
    {
        builder.ToTable("Baselines");
        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();
        builder.Property(b => b.Description).HasMaxLength(2000);

        builder.HasIndex(b => new { b.WorkspaceId, b.ProjectId, b.Name }).IsUnique();
        builder.HasIndex(b => new { b.ProjectId, b.EnvironmentId });

        builder.HasMany(b => b.Versions)
            .WithOne(v => v.Baseline!)
            .HasForeignKey(v => v.BaselineId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BaselineVersionConfiguration : IEntityTypeConfiguration<BaselineVersion>
{
    public void Configure(EntityTypeBuilder<BaselineVersion> builder)
    {
        builder.ToTable("BaselineVersions");
        builder.Property(v => v.ContentType).HasMaxLength(200);
        builder.Property(v => v.Description).HasMaxLength(2000);
        builder.Property(v => v.RejectionReason).HasMaxLength(2000);
        builder.Property(v => v.NormalizedHash).HasMaxLength(64);

        // Numbers are shown to people and referenced in reports, so two versions sharing one
        // would make "version 3" ambiguous in exactly the conversation where it matters.
        builder.HasIndex(v => new { v.BaselineId, v.Number }).IsUnique();
        builder.HasIndex(v => new { v.BaselineId, v.Status });
    }
}

public sealed class BaselineRuleConfiguration : IEntityTypeConfiguration<BaselineRule>
{
    public void Configure(EntityTypeBuilder<BaselineRule> builder)
    {
        builder.ToTable("BaselineRules");
        builder.Property(r => r.Path).HasMaxLength(500).IsRequired();
        builder.Property(r => r.Matcher).HasMaxLength(60).IsRequired();
        builder.Property(r => r.Text).HasMaxLength(2000);
        builder.Property(r => r.Note).HasMaxLength(500);

        builder.HasIndex(r => new { r.BaselineId, r.SortOrder });

        builder.HasOne(r => r.Baseline)
            .WithMany()
            .HasForeignKey(r => r.BaselineId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
