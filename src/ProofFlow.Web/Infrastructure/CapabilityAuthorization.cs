using Microsoft.AspNetCore.Authorization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// One authorization policy per capability, so an action declares what it needs rather than which
/// roles happen to have it today.
///
/// <c>[Authorize(Policy = Policies.ApproveBaseline)]</c> keeps working when the role table changes;
/// <c>[Authorize(Roles = "Admin,Reviewer")]</c> does not, and nothing tells you when it stops
/// being right.
/// </summary>
public static class CapabilityAuthorization
{
    public static IServiceCollection AddCapabilityAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, CapabilityHandler>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(null)
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        services.AddAuthorization(options =>
        {
            foreach (var capability in Enum.GetValues<Capability>())
            {
                options.AddPolicy(Policies.For(capability), policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new CapabilityRequirement(capability));
                });
            }
        });

        return services;
    }
}

/// <summary>Policy names, derived from the capability so the two cannot drift apart.</summary>
public static class Policies
{
    public static string For(Capability capability) => $"cap:{capability}";

    public const string ViewProject = "cap:ViewProject";
    public const string ManageProject = "cap:ManageProject";
    public const string ManageMembers = "cap:ManageMembers";
    public const string ManageEnvironment = "cap:ManageEnvironment";
    public const string ManageSecret = "cap:ManageSecret";
    public const string ViewSecret = "cap:ViewSecret";
    public const string CreateTest = "cap:CreateTest";
    public const string EditTest = "cap:EditTest";
    public const string DeleteTest = "cap:DeleteTest";
    public const string RunTest = "cap:RunTest";
    public const string CancelRun = "cap:CancelRun";
    public const string DeleteRun = "cap:DeleteRun";
    public const string RecordBaseline = "cap:RecordBaseline";
    public const string ApproveBaseline = "cap:ApproveBaseline";
    public const string ManageDataSet = "cap:ManageDataSet";
    public const string ManageSchedule = "cap:ManageSchedule";
    public const string ManageRunner = "cap:ManageRunner";
    public const string ViewAudit = "cap:ViewAudit";
    public const string ViewRun = "cap:ViewRun";
    public const string ExportProject = "cap:ExportProject";
    public const string ImportProject = "cap:ImportProject";
}

public sealed class CapabilityRequirement(Capability capability) : IAuthorizationRequirement
{
    public Capability Capability { get; } = capability;
}

public sealed class CapabilityHandler(IHttpContextAccessor accessor)
    : AuthorizationHandler<CapabilityRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, CapabilityRequirement requirement)
    {
        // Resolved per call rather than injected: the handler is a singleton (policies are), and
        // ICurrentUser is scoped to the request.
        var currentUser = accessor.HttpContext?.RequestServices.GetService<ICurrentUser>();

        if (currentUser?.Can(requirement.Capability) == true)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
