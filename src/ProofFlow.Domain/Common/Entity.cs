namespace ProofFlow.Domain.Common;

/// <summary>
/// What every stored thing has: an identity and the two moments that bound its life.
///
/// The id is a version-7 GUID rather than a random one. Version 7 puts a timestamp in the leading
/// bits, so consecutive inserts land next to each other in the primary-key index instead of
/// scattering across it. ProofFlow writes node-run and run-event rows in bursts of hundreds, which
/// is exactly the workload random GUIDs punish.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Rows that belong to one workspace and must never be read across the boundary.
///
/// Implementing this is what puts an entity behind the global query filter. Anything that holds
/// customer data implements it; platform-wide reference data does not.
/// </summary>
public interface IWorkspaceOwned
{
    Guid WorkspaceId { get; }
}
