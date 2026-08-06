using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProofFlow.Domain.Environments;

namespace ProofFlow.Infrastructure.Persistence.Configurations;

public sealed class ProjectEnvironmentConfiguration : IEntityTypeConfiguration<ProjectEnvironment>
{
    public void Configure(EntityTypeBuilder<ProjectEnvironment> builder)
    {
        builder.ToTable("Environments");
        builder.Property(e => e.Name).HasMaxLength(120).IsRequired();
        builder.Property(e => e.Slug).HasMaxLength(80).IsRequired();
        builder.Property(e => e.BaseUrl).HasMaxLength(2000);
        builder.Property(e => e.ProxyUrl).HasMaxLength(500);
        builder.Property(e => e.AllowedHosts).HasMaxLength(4000);

        builder.HasIndex(e => new { e.ProjectId, e.Slug }).IsUnique();

        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.Variables)
            .WithOne(v => v.Environment!)
            .HasForeignKey(v => v.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EnvironmentVariableConfiguration : IEntityTypeConfiguration<EnvironmentVariable>
{
    public void Configure(EntityTypeBuilder<EnvironmentVariable> builder)
    {
        builder.ToTable("Variables");
        builder.Property(v => v.Name).HasMaxLength(120).IsRequired();
        builder.Property(v => v.Value).HasMaxLength(8000);
        builder.Property(v => v.Description).HasMaxLength(500);

        // Unique per environment, and separately unique per project for the environment-wide row.
        // Two rows with the same name in one scope would make {{vars.x}} resolve by row order.
        builder.HasIndex(v => new { v.ProjectId, v.EnvironmentId, v.Name }).IsUnique();
    }
}

public sealed class SecretConfiguration : IEntityTypeConfiguration<Secret>
{
    public void Configure(EntityTypeBuilder<Secret> builder)
    {
        builder.ToTable("Secrets");
        builder.Property(s => s.Name).HasMaxLength(120).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);
        builder.Property(s => s.Ciphertext).IsRequired();
        builder.Property(s => s.Nonce).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Tag).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Preview).HasMaxLength(8);

        builder.HasIndex(s => new { s.ProjectId, s.EnvironmentId, s.Name }).IsUnique();

        builder.HasOne(s => s.Environment)
            .WithMany()
            .HasForeignKey(s => s.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
