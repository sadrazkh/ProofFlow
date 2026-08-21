using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Auditing;
using ProofFlow.Domain.Tagging;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.Property(a => a.Action).HasMaxLength(120).IsRequired();
        builder.Property(a => a.ActorDisplay).HasMaxLength(200).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(80);
        builder.Property(a => a.TargetLabel).HasMaxLength(300);
        builder.Property(a => a.IpAddress).HasMaxLength(64);

        // The log is read newest-first, filtered by workspace and often by project. This is the
        // index that query wants; without it the page gets slower every day it stays useful.
        builder.HasIndex(a => new { a.WorkspaceId, a.OccurredAt });
        builder.HasIndex(a => new { a.WorkspaceId, a.ProjectId, a.OccurredAt });
        builder.HasIndex(a => new { a.WorkspaceId, a.Action });
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<ProofFlow.Domain.Notifications.Notification>
{
    public void Configure(EntityTypeBuilder<ProofFlow.Domain.Notifications.Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.Property(n => n.Kind).HasMaxLength(60).IsRequired();
        builder.Property(n => n.ArgsJson).HasMaxLength(2000);
        builder.Property(n => n.LinkPath).HasMaxLength(400);
        builder.Property(n => n.TargetType).HasMaxLength(80);
        builder.Property(n => n.TargetLabel).HasMaxLength(300);
        builder.Property(n => n.WebhookFailure).HasMaxLength(500);

        // The bell reads newest-first per workspace; the delivery worker sweeps by what is owed.
        builder.HasIndex(n => new { n.WorkspaceId, n.CreatedAt });
        builder.HasIndex(n => new { n.ProjectId, n.WebhookAt, n.EmailedAt });
    }
}

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.Property(t => t.Name).HasMaxLength(60).IsRequired();
        builder.Property(t => t.Accent).HasMaxLength(24).IsRequired();
        builder.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique();
    }
}

public sealed class TagAssignmentConfiguration : IEntityTypeConfiguration<TagAssignment>
{
    public void Configure(EntityTypeBuilder<TagAssignment> builder)
    {
        builder.ToTable("TagAssignments");
        builder.Property(t => t.TargetType).HasMaxLength(80).IsRequired();
        builder.HasIndex(t => new { t.TagId, t.TargetType, t.TargetId }).IsUnique();
        builder.HasIndex(t => new { t.TargetType, t.TargetId });

        builder.HasOne(t => t.Tag)
            .WithMany()
            .HasForeignKey(t => t.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
