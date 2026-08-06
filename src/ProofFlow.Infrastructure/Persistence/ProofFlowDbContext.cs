using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Auditing;
using ProofFlow.Domain.Common;
using ProofFlow.Domain.Projects;
using ProofFlow.Domain.Tagging;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;

namespace ProofFlow.Infrastructure.Persistence;

/// <summary>
/// The model, shared by both providers.
///
/// Derived per provider (see <see cref="SqliteProofFlowDbContext"/> and
/// <see cref="PostgresProofFlowDbContext"/>) so each keeps its own migration history. One shared
/// migration set cannot work: the two providers disagree about column types for JSON, timestamps
/// and identity columns, and a migration generated against one silently produces the wrong DDL on
/// the other.
///
/// Roles come from <c>IdentityUserContext</c> rather than <c>IdentityDbContext</c>: authorisation
/// here is by workspace membership, so Identity's own role tables would sit empty and invite
/// someone to start using them as a second, disagreeing source of truth.
/// </summary>
public abstract class ProofFlowDbContext(DbContextOptions options, IWorkspaceScope scope)
    : IdentityUserContext<ProofFlowUser, Guid>(options)
{
    /// <summary>
    /// Exposed so infrastructure services can ask what tenant they are inside without taking a
    /// second dependency that could disagree with the one the filters use.
    /// </summary>
    public IWorkspaceScope Scope { get; } = scope;

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagAssignment> TagAssignments => Set<TagAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ProofFlowDbContext).Assembly);

        // Identity's own tables get shorter names than the framework default. They are ours now.
        builder.Entity<ProofFlowUser>(b =>
        {
            b.ToTable("Users");
            b.Property(u => u.DisplayName).HasMaxLength(200);
            b.Property(u => u.PreferredCulture).HasMaxLength(8);
            b.Property(u => u.ThemeChoice).HasMaxLength(16);
        });
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("UserTokens");

        ApplyWorkspaceFilters(builder);
    }

    private static readonly MethodInfo FilterOne = typeof(ProofFlowDbContext)
        .GetMethod(nameof(ApplyWorkspaceFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// The tenant boundary, applied once to every entity that declares itself workspace-owned.
    ///
    /// Driven off the model rather than written per entity, because the failure mode of forgetting
    /// one is that a project appears in another workspace's list — which looks like nothing at all
    /// until it looks like a breach.
    /// </summary>
    private void ApplyWorkspaceFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is not null) continue;
            if (!typeof(IWorkspaceOwned).IsAssignableFrom(entityType.ClrType)) continue;

            FilterOne.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
        }
    }

    /// <summary>
    /// One entity's filter, written as an ordinary lambda over context state — the shape EF Core
    /// recognises and lifts into a per-query parameter, so the filter follows the current request
    /// rather than whichever request happened to build the model first.
    ///
    /// Two details are load-bearing:
    ///
    /// The <c>IsSystem</c> escape is what keeps background work alive. Without it a scheduler or a
    /// sweeper, which has no request and therefore no workspace, reads an empty database, does
    /// nothing, and reports success.
    ///
    /// The comparison is widened to <c>Guid?</c> instead of reading <c>WorkspaceId.Value</c>.
    /// Parameter extraction evaluates that subtree eagerly, before the null check that guards it
    /// has any chance to short-circuit, so <c>.Value</c> would throw on every query made outside a
    /// workspace. Comparing nullables yields no rows instead, which is the intended answer.
    /// </summary>
    private void ApplyWorkspaceFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, IWorkspaceOwned
    {
        builder.Entity<TEntity>()
            .HasQueryFilter(e => Scope.IsSystem || (Guid?)e.WorkspaceId == Scope.WorkspaceId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Fills in the three things every write needs and nobody should have to remember: the
    /// timestamps, and the workspace a new row belongs to.
    ///
    /// Timestamps are forced to UTC. Npgsql maps <c>DateTimeOffset</c> to <c>timestamptz</c> and
    /// rejects any value whose offset is not zero, so a machine in Tehran would otherwise throw on
    /// every insert while the same code passed on SQLite.
    /// </summary>
    private void Stamp()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAt == default) entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;

                    if (entry.Entity is IWorkspaceOwned { WorkspaceId: var id } && id == Guid.Empty
                        && Scope.WorkspaceId is { } current)
                    {
                        entry.Property(nameof(IWorkspaceOwned.WorkspaceId)).CurrentValue = current;
                    }
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // The workspace a row belongs to is not a thing that changes. Reassigning it
                    // would move data between tenants, so the write is refused rather than audited.
                    if (entry.Entity is IWorkspaceOwned)
                    {
                        var property = entry.Property(nameof(IWorkspaceOwned.WorkspaceId));
                        if (property.IsModified)
                            throw new InvalidOperationException(
                                $"{entry.Entity.GetType().Name} tried to change workspace. Rows do not move between tenants.");
                    }
                    break;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProofFlowUser>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAt == default)
                entry.Entity.CreatedAt = now;
        }
    }
}
