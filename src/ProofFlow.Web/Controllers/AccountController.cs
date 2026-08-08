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
    SessionCookie session,
    ProofFlowDbContext db,
    IClock clock,
    IAuditLog audit,
    AccountMail mail,
    IWebHostEnvironment environment,
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

    // ---- forgotten passwords ---------------------------------------------------------------------

    [HttpGet("forgot")]
    public IActionResult Forgot()
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        return View(new ForgotPasswordViewModel());
    }

    /// <summary>
    /// Sends a reset link, and says the same thing either way.
    ///
    /// The response cannot depend on whether the address belongs to an account. "No such account" on
    /// this form is a way of asking whether somebody has one, and it works for every address anybody
    /// cares to try. So the answer is identical, the work is not, and the rate limiter is what makes
    /// the timing difference not worth measuring.
    /// </summary>
    [HttpPost("forgot")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Forgot(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByEmailAsync(model.Email);

        if (user is not null)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = mail.ResetLink(user.Id, token);

            if (mail.CanSend)
            {
                await mail.PasswordResetAsync(user.Email!, link, HttpContext.RequestAborted);
            }
            else if (environment.IsDevelopment())
            {
                // A development machine with no mail server would otherwise have no way to walk this
                // flow at all. Carried once, through TempData, and only here — on a deployed
                // installation this branch does not exist, because showing the link to whoever typed
                // the address is the whole vulnerability the flow is designed to avoid.
                TempData["ResetLink"] = link;
            }
            else
            {
                // Nothing was sent and nothing can be shown. Somebody has to be able to find out
                // why, and it is not the person at the form.
                logger.LogWarning(
                    "A password reset was requested but no mail server is configured. "
                    + "Set Smtp:Host, or reset the password from the command line.");
            }

            await audit.RecordAsync(
                new AuditEntry("user.passwordResetRequested", null, "User", user.Id),
                HttpContext.RequestAborted);
        }

        return RedirectToAction(nameof(CheckEmail));
    }

    [HttpGet("check-email")]
    public IActionResult CheckEmail()
    {
        return View(new CheckEmailViewModel
        {
            Link = TempData["ResetLink"] as string,
            WasEmailed = mail.CanSend,
        });
    }

    [HttpGet("reset")]
    public async Task<IActionResult> Reset(Guid u, string? t)
    {
        if (string.IsNullOrWhiteSpace(t) || await users.FindByIdAsync(u.ToString()) is null)
        {
            TempData.Error(localizer["auth.reset.notUsable"]);
            return Redirect("/account/forgot");
        }

        // The token is not verified here. Identity's tokens are single-use and time-limited, and
        // spending one to render a form would mean a reload of the page invalidates it.
        return View(new ResetPasswordViewModel { UserId = u, Token = t });
    }

    [HttpPost("reset")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Reset(ResetPasswordViewModel model)
    {
        if (model.Password != model.ConfirmPassword)
            ModelState.AddModelError(nameof(model.ConfirmPassword), localizer["auth.passwordsDiffer"]);

        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByIdAsync(model.UserId.ToString());

        if (user is null)
        {
            TempData.Error(localizer["auth.reset.notUsable"]);
            return Redirect("/account/forgot");
        }

        var result = await users.ResetPasswordAsync(user, model.Token, model.Password);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                // An expired or spent token is not a field-level problem, and putting it under the
                // password box would tell somebody their new password was rejected when it was not.
                ModelState.AddModelError(
                    error.Code.Contains("Token", StringComparison.Ordinal) ? string.Empty : nameof(model.Password),
                    error.Code switch
                    {
                        "InvalidToken" => localizer["auth.reset.notUsable"],
                        "PasswordTooShort" => localizer["auth.passwordTooShort", 10],
                        _ => error.Description,
                    });
            }

            return View(model);
        }

        // Somebody locked out by the guessing that made them reset in the first place should not
        // then be locked out of the account they have just recovered.
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);

        await audit.RecordAsync(
            new AuditEntry("user.passwordReset", null, "User", user.Id), HttpContext.RequestAborted);

        // Not signed in automatically. Whoever holds the link is not yet known to be the account
        // holder; typing the new password once proves they at least know it.
        TempData.Success(localizer["auth.reset.done"]);
        return Redirect("/account/sign-in");
    }

    /// <summary>
    /// Writes the sign-in cookie.
    ///
    /// Delegated, because accepting an invitation has to write the same cookie with the same claims
    /// — and two copies of that would be two places for the workspace claim to be forgotten.
    /// </summary>
    private Task IssueCookieAsync(ProofFlowUser user, bool rememberMe) =>
        session.IssueAsync(user, rememberMe);

    private Task<bool> AnyUserExistsAsync() => db.Users.AnyAsync();

    /// <summary>
    /// Only ever redirects inside this application. An open redirect on a sign-in form is a
    /// credential-phishing tool: the link genuinely comes from us, and lands somewhere else.
    /// </summary>
    private IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : Redirect("/");
}
