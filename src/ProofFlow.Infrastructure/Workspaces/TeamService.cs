using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Workspaces;

/// <summary>
/// Who is in the workspace, and what they may do.
///
/// Two rules hold everything else up, and both are about the same failure — a workspace that
/// somebody has locked themselves out of.
///
/// The last owner cannot be demoted or removed. Not "should not": cannot, at this layer, whatever
/// the interface offers, because the interface is not the only caller and a workspace with no owner
/// is one nobody can ever repair.
///
/// And nobody can change their own role. Somebody who could promote themselves does not have a
/// role, they have a suggestion.
/// </summary>
public sealed class TeamService(ProofFlowDbContext db, IClock clock, ICurrentUser me)
{
    /// <summary>Random bytes in an invitation token. Guessing one is not a strategy.</summary>
    public const int TokenBytes = 32;

    public async Task<IReadOnlyList<TeamMember>> MembersAsync(
        Guid workspaceId, CancellationToken cancellation = default)
    {
        var members = await db.WorkspaceMembers
            .Where(member => member.WorkspaceId == workspaceId)
            .ToListAsync(cancellation);

        var ids = members.Select(member => member.UserId).ToList();

        // Read from Identity's table rather than joined, because the membership belongs to this
        // model and the account does not — and the two are allowed to be in different databases
        // later without this query becoming a lie.
        var users = await db.Users
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Email, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, cancellation);

        return
        [
            .. members
                .OrderBy(member => member.Role)
                .ThenBy(member => users.GetValueOrDefault(member.UserId)?.DisplayName ?? string.Empty)
                .Select(member => new TeamMember(
                    member.UserId,
                    users.GetValueOrDefault(member.UserId)?.DisplayName ?? "—",
                    users.GetValueOrDefault(member.UserId)?.Email,
                    member.Role,
                    member.JoinedAt,
                    member.UserId == me.UserId)),
        ];
    }

    /// <summary>
    /// Makes an invitation and returns the token, once.
    ///
    /// Returned rather than emailed from here: sending is somebody else's job, and a service that
    /// both mints credentials and talks to an SMTP server is one that cannot be tested without one.
    /// </summary>
    public async Task<(WorkspaceInvitation Invitation, string Token)> InviteAsync(
        Guid workspaceId, string email, WorkspaceRole role, CancellationToken cancellation = default)
    {
        var address = email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@'))
            throw new InvalidOperationException("That is not an email address.");

        if (role == WorkspaceRole.Owner)
            throw new InvalidOperationException("Ownership is transferred, not invited.");

        var already = await db.WorkspaceMembers
            .Where(member => member.WorkspaceId == workspaceId)
            .Join(db.Users, member => member.UserId, user => user.Id, (member, user) => user.Email)
            .AnyAsync(existing => existing != null && existing.ToLower() == address, cancellation);

        if (already) throw new InvalidOperationException("That person is already in this workspace.");

        // Any earlier open invitation to the same address is withdrawn. Two live links to one
        // mailbox is two ways in, and only one of them is the one somebody meant.
        foreach (var open in await db.WorkspaceInvitations
                     .Where(invitation => invitation.WorkspaceId == workspaceId
                                          && invitation.Email == address
                                          && invitation.AcceptedAt == null
                                          && invitation.RevokedAt == null)
                     .ToListAsync(cancellation))
        {
            open.RevokedAt = clock.UtcNow;
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

        var invitation = new WorkspaceInvitation
        {
            WorkspaceId = workspaceId,
            Email = address,
            Role = role,
            Hash = Fingerprint(token),
            InvitedByUserId = me.UserId,
            ExpiresAt = clock.UtcNow + WorkspaceInvitation.Lifetime,
        };

        db.WorkspaceInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellation);

        return (invitation, token);
    }

    /// <summary>The invitation a token opens, or nothing.</summary>
    public async Task<WorkspaceInvitation?> FindAsync(
        string? token, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Fingerprint(token.Trim());

        // Across workspaces, because a token is what says which workspace this is.
        var invitation = await db.WorkspaceInvitations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Hash == hash, cancellation);

        return invitation?.IsOpen(clock.UtcNow) == true ? invitation : null;
    }

    /// <summary>
    /// Turns an open invitation into a membership.
    ///
    /// Idempotent on the membership: somebody who is already a member and clicks the link again
    /// keeps the role they have rather than being silently demoted to the one in the invitation.
    /// </summary>
    public async Task<bool> AcceptAsync(
        WorkspaceInvitation invitation, Guid userId, CancellationToken cancellation = default)
    {
        if (!invitation.IsOpen(clock.UtcNow)) return false;

        var existing = await db.WorkspaceMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(member => member.WorkspaceId == invitation.WorkspaceId
                                           && member.UserId == userId, cancellation);

        if (existing is null)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = invitation.WorkspaceId,
                UserId = userId,
                Role = invitation.Role,
                InvitedAt = invitation.CreatedAt,
                JoinedAt = clock.UtcNow,
            });
        }
        else if (existing.JoinedAt is null)
        {
            existing.JoinedAt = clock.UtcNow;
        }

        invitation.AcceptedAt = clock.UtcNow;
        invitation.AcceptedByUserId = userId;

        await db.SaveChangesAsync(cancellation);
        return true;
    }

    public async Task<bool> RevokeInvitationAsync(
        Guid workspaceId, Guid invitationId, CancellationToken cancellation = default)
    {
        var invitation = await db.WorkspaceInvitations
            .FirstOrDefaultAsync(candidate => candidate.Id == invitationId
                                              && candidate.WorkspaceId == workspaceId, cancellation);

        if (invitation is null || invitation.AcceptedAt is not null) return false;

        invitation.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellation);

        return true;
    }

    /// <summary>
    /// Changes somebody's role, or says why not.
    ///
    /// A code rather than a sentence: this layer knows the rules and the web layer knows the
    /// language.
    /// </summary>
    public async Task<string?> ChangeRoleAsync(
        Guid workspaceId, Guid userId, WorkspaceRole role, CancellationToken cancellation = default)
    {
        if (userId == me.UserId) return "team.notYourself";

        var member = await db.WorkspaceMembers
            .FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId
                                              && candidate.UserId == userId, cancellation);

        if (member is null) return "team.noSuchMember";
        if (member.Role == role) return null;

        if (member.Role == WorkspaceRole.Owner && await LastOwnerAsync(workspaceId, userId, cancellation))
            return "team.lastOwner";

        member.Role = role;
        await db.SaveChangesAsync(cancellation);

        return null;
    }

    public async Task<string?> RemoveAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellation = default)
    {
        if (userId == me.UserId) return "team.notYourself";

        var member = await db.WorkspaceMembers
            .FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId
                                              && candidate.UserId == userId, cancellation);

        if (member is null) return "team.noSuchMember";

        if (member.Role == WorkspaceRole.Owner && await LastOwnerAsync(workspaceId, userId, cancellation))
            return "team.lastOwner";

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(cancellation);

        return null;
    }

    /// <summary>Whether this person is the only owner left, which makes them unremovable.</summary>
    private async Task<bool> LastOwnerAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellation)
    {
        var another = await db.WorkspaceMembers
            .AnyAsync(member => member.WorkspaceId == workspaceId
                                && member.Role == WorkspaceRole.Owner
                                && member.UserId != userId, cancellation);

        return !another;
    }

    /// <summary>
    /// The one place a token becomes a stored value.
    ///
    /// Public so the demo seeder makes its invitation the same way the interface does, rather than
    /// growing a second hash that could drift from this one.
    /// </summary>
    public static string Fingerprint(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>One person in the workspace, as the team page shows them.</summary>
public sealed record TeamMember(
    Guid UserId,
    string DisplayName,
    string? Email,
    WorkspaceRole Role,
    DateTimeOffset? JoinedAt,

    /// <summary>True for the reader, so the page can say "you" and disable the controls.</summary>
    bool IsYou);
