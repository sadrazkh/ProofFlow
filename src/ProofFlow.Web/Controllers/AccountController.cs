using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Identity;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

[AllowAnonymous]
[Route("account")]
public sealed class AccountController(
    UserManager<ProofFlowUser> users,
    SignInManager<ProofFlowUser> signIn,
    ProofFlowDbContext db,
    IClock clock,
    IAuditLog audit,
    IStringLocalizer localizer,
    ILogger<AccountController> logger) : Controller
{
    [HttpGet("sign-in")]
    public IActionResult SignIn(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        return View(new SignInViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost("sign-in")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SignIn(SignInViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByEmailAsync(model.Email);
        if (user is null)
        {
            // The same message whether the account exists or the password was wrong. Two different
            // answers here is an account-enumeration oracle, and a slow one is still an oracle.
            ModelState.AddModelError(string.Empty, localizer["auth.invalidCredentials"]);
            return View(model);
        }

        var result = await signIn.CheckPasswordSignInAsync(user, model.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, localizer["auth.lockedOut"]);
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, localizer["auth.invalidCredentials"]);
            return View(model);
        }

        user.LastSignInAt = clock.UtcNow;
        await users.UpdateAsync(user);

        await IssueCookieAsync(user, model.RememberMe);

        // After the principal is in place, so the entry lands in the right workspace with the
        // right actor rather than being dropped as unscoped.
        await audit.RecordAsync(new AuditEntry("user.signedIn"), HttpContext.RequestAborted);

        return SafeRedirect(model.ReturnUrl);
    }

    [HttpGet("sign-up")]
    public async Task<IActionResult> SignUp()
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");

        return View(new SignUpViewModel { IsFirstAccount = !await AnyUserExistsAsync() });
    }

    [HttpPost("sign-up")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SignUp(SignUpViewModel model)
    {
        model.IsFirstAccount = !await AnyUserExistsAsync();

        if (model.Password != model.ConfirmPassword)
            ModelState.AddModelError(nameof(model.ConfirmPassword), localizer["auth.passwordsDiffer"]);

        if (!ModelState.IsValid) return View(model);

        // Only the first account may create a workspace from the sign-up form. Everyone else
        // arrives by invitation — otherwise the second colleague to sign up silently gets their
        // own empty workspace and wonders where the project went.
        if (!model.IsFirstAccount)
        {
            ModelState.AddModelError(string.Empty, localizer["auth.deniedBody"]);
            return View(model);
        }

        var user = new ProofFlowUser
        {
            Id = Guid.CreateVersion7(),
            UserName = model.Email,
            Email = model.Email,
            DisplayName = model.DisplayName,
            CreatedAt = clock.UtcNow,
        };

        var created = await users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors)
            {
                ModelState.AddModelError(
                    error.Code.Contains("Password", StringComparison.Ordinal) ? nameof(model.Password) : string.Empty,
                    error.Code switch
                    {
                        "DuplicateUserName" or "DuplicateEmail" => localizer["auth.emailTaken"],
                        "PasswordTooShort" => localizer["auth.passwordTooShort", 10],
                        _ => error.Description,
                    });
            }
            return View(model);
        }

        var workspaceName = string.IsNullOrWhiteSpace(model.WorkspaceName)
            ? model.DisplayName
            : model.WorkspaceName;

        var workspace = new Workspace
        {
            Name = workspaceName,
            Slug = Slug.From(workspaceName, "workspace"),
            CreatedByUserId = user.Id,
        };

        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = clock.UtcNow,
        });

        user.LastWorkspaceId = workspace.Id;
        await db.SaveChangesAsync();
        await users.UpdateAsync(user);

        logger.LogInformation("The first account was created and opened workspace {Workspace}.", workspace.Slug);

        await IssueCookieAsync(user, rememberMe: true);
        return Redirect("/");
    }

    [HttpPost("sign-out")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutPost()
    {
        await signIn.SignOutAsync();
        TempData.Info(localizer["auth.signedOut"]);
        return Redirect("/account/sign-in");
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();

    /// <summary>
    /// Writes the sign-in cookie, including the workspace and role claims that authorisation and
    /// the tenant filter both read.
    ///
    /// Claims rather than a per-request lookup: <c>Can()</c> is called dozens of times rendering
    /// one page. The cost is that a role change lands on the next sign-in or workspace switch,
    /// which is stated in <see cref="HttpCurrentUser"/>.
    /// </summary>
    private async Task IssueCookieAsync(ProofFlowUser user, bool rememberMe)
    {
        var workspaceId = user.LastWorkspaceId;
        var membership = await db.WorkspaceMembers
            .IgnoreQueryFilters() // No workspace is established yet — that is what this reads.
            .Where(m => m.UserId == user.Id)
            .OrderByDescending(m => m.WorkspaceId == workspaceId)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        var identity = await signIn.CreateUserPrincipalAsync(user);
        if (identity.Identity is ClaimsIdentity claims)
        {
            claims.AddClaim(new Claim("pf:name", user.DisplayName ?? user.Email ?? "unknown"));

            if (membership is not null)
            {
                claims.AddClaim(new Claim(HttpCurrentUser.WorkspaceClaim, membership.WorkspaceId.ToString()));
                claims.AddClaim(new Claim(HttpCurrentUser.RoleClaim, membership.Role.ToString()));
            }
        }

        await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, identity,
            new AuthenticationProperties { IsPersistent = rememberMe });

        // SignInAsync writes the cookie for the *next* request; it does not change who this one is
        // running as. Without this line, anything after sign-in still sees an anonymous principal —
        // which is why the audit entry for signing in used to be dropped for having no workspace,
        // and why the tenant filter would have returned nothing to any query that followed.
        HttpContext.User = identity;
    }

    private Task<bool> AnyUserExistsAsync() => db.Users.AnyAsync();

    /// <summary>
    /// Only ever redirects inside this application. An open redirect on a sign-in form is a
    /// credential-phishing tool: the link genuinely comes from us, and lands somewhere else.
    /// </summary>
    private IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : Redirect("/");
}
