using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Baselines;

/// <summary>
/// Whether the person asking may approve this.
///
/// The separation of author and approver is the entire point of having a review. A reviewer who
/// blesses their own recording has not reviewed anything — they have added a step to their own
/// workflow and a signature to a report that now claims two people looked.
///
/// But it is conditional, and that is the decision worth stating: the rule is "somebody else has to
/// look, if there is somebody else". A workspace of one person is not a governance failure, it is a
/// workspace of one person, and a product that refuses to let them approve anything is a product
/// they cannot use. The moment a second reviewer exists, the rule binds.
/// </summary>
public sealed class Separation(ProofFlowDbContext db, ICurrentUser me)
{
    /// <summary>
    /// Why this person may not approve this thing, or null when they may.
    ///
    /// A code rather than a sentence, the same as everywhere else that crosses out of a service:
    /// the web layer knows about languages and this does not.
    /// </summary>
    public async Task<string?> RefusalAsync(Guid? authorId, CancellationToken cancellation = default)
    {
        if (!me.Can(Capability.ApproveBaseline)) return "approval.notAllowed";

        // A machine has no self to separate from, and an unattributed recording has nobody to
        // separate from either. Both fall through to the capability check above.
        if (authorId is null || me.UserId is null) return null;
        if (authorId != me.UserId) return null;

        return await SomebodyElseAsync(cancellation) ? "approval.notYourOwn" : null;
    }

    /// <summary>
    /// Whether anybody else in this workspace could approve instead.
    ///
    /// Read from the roles rather than from a count of members: three viewers and one designer is a
    /// workspace where nobody else can approve, and telling the designer to find a reviewer who
    /// does not exist is worse than letting them approve.
    /// </summary>
    public async Task<bool> SomebodyElseAsync(CancellationToken cancellation = default)
    {
        if (me.WorkspaceId is not { } workspaceId) return false;

        var roles = await db.WorkspaceMembers
            .Where(member => member.WorkspaceId == workspaceId
                             && member.UserId != me.UserId
                             && member.JoinedAt != null)
            .Select(member => member.Role)
            .ToListAsync(cancellation);

        return roles.Any(role => RoleCapabilities.Allows(role, Capability.ApproveBaseline));
    }
}
