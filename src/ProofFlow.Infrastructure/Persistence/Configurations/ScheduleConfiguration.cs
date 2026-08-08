using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Scheduling;
using ProofFlow.Domain.Workspaces;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class RunScheduleConfiguration : IEntityTypeConfiguration<RunSchedule>
{
    public void Configure(EntityTypeBuilder<RunSchedule> builder)
    {
        builder.ToTable("RunSchedules");
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Cron).HasMaxLength(120).IsRequired();
        builder.Property(s => s.TimeZoneId).HasMaxLength(80).IsRequired();
        builder.Property(s => s.Problem).HasMaxLength(500);

        builder.HasIndex(s => new { s.ProjectId, s.Name });

        // The scheduler's own query, and the only one it makes on every tick: what is due. Enabled
        // first because most rows are not due and the filter on it is the cheap half.
        builder.HasIndex(s => new { s.Enabled, s.NextRunAt });

        builder.HasMany(s => s.Scenarios)
            .WithOne(link => link.Schedule!)
            .HasForeignKey(link => link.RunScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Environments)
            .WithOne(link => link.Schedule!)
            .HasForeignKey(link => link.RunScheduleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ScheduleScenarioConfiguration : IEntityTypeConfiguration<ScheduleScenario>
{
    public void Configure(EntityTypeBuilder<ScheduleScenario> builder)
    {
        builder.ToTable("ScheduleScenarios");
        builder.HasIndex(link => new { link.RunScheduleId, link.ScenarioId }).IsUnique();

        // Cascade from the scenario as well: a schedule pointing at a deleted test would fire every
        // morning and fail in a way nobody can act on.
        builder.HasOne(link => link.Scenario)
            .WithMany()
            .HasForeignKey(link => link.ScenarioId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ScheduleEnvironmentConfiguration : IEntityTypeConfiguration<ScheduleEnvironment>
{
    public void Configure(EntityTypeBuilder<ScheduleEnvironment> builder)
    {
        builder.ToTable("ScheduleEnvironments");
        builder.HasIndex(link => new { link.RunScheduleId, link.EnvironmentId }).IsUnique();

        builder.HasOne(link => link.Environment)
            .WithMany()
            .HasForeignKey(link => link.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");
        builder.Property(key => key.Name).HasMaxLength(200).IsRequired();
        builder.Property(key => key.Hash).HasMaxLength(64).IsRequired();
        builder.Property(key => key.Preview).HasMaxLength(24).IsRequired();

        // The lookup every authenticated request makes, and it has to be by hash alone: the caller
        // presents a key and nothing else, so there is no workspace to narrow by yet.
        builder.HasIndex(key => key.Hash).IsUnique();
        builder.HasIndex(key => new { key.WorkspaceId, key.RevokedAt });
    }
}

public sealed class WorkspaceInvitationConfiguration : IEntityTypeConfiguration<WorkspaceInvitation>
{
    public void Configure(EntityTypeBuilder<WorkspaceInvitation> builder)
    {
        builder.ToTable("WorkspaceInvitations");
        builder.Property(invitation => invitation.Email).HasMaxLength(320).IsRequired();
        builder.Property(invitation => invitation.Hash).HasMaxLength(64).IsRequired();

        // The lookup a link makes, and it has to be by hash alone: somebody following an invitation
        // has presented a token and nothing else, so there is no workspace to narrow by yet.
        builder.HasIndex(invitation => invitation.Hash).IsUnique();
        builder.HasIndex(invitation => new { invitation.WorkspaceId, invitation.Email });
    }
}
