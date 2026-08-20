using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Workspaces;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("Workspaces");
        builder.Property(w => w.Name).HasMaxLength(200).IsRequired();
        builder.Property(w => w.Slug).HasMaxLength(80).IsRequired();
        builder.HasIndex(w => w.Slug).IsUnique();

        builder.HasMany(w => w.Members)
            .WithOne(m => m.Workspace!)
            .HasForeignKey(m => m.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> builder)
    {
        builder.ToTable("WorkspaceMembers");

        // One membership per person per workspace. Without this, an invitation accepted twice
        // creates a second row, and which of the two roles applies becomes a matter of row order.
        builder.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique();
    }
}

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Slug).HasMaxLength(80).IsRequired();
        builder.Property(p => p.Accent).HasMaxLength(24).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.BadgeHash).HasMaxLength(64);
        builder.Property(p => p.BadgePreview).HasMaxLength(16);
        builder.Ignore(p => p.IsArchived);

        builder.HasIndex(p => new { p.WorkspaceId, p.Slug }).IsUnique();

        // The anonymous badge endpoint looks a project up by nothing else.
        builder.HasIndex(p => p.BadgeHash);

        builder.HasOne(p => p.Workspace)
            .WithMany()
            .HasForeignKey(p => p.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
