using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.Application.Common;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Environments;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Web.Infrastructure;
using ProofFlow.Web.ViewModels;

namespace ProofFlow.Web.Controllers;

/// <summary>
/// Environments, their variables, and their secrets.
///
/// One controller for all three because they are one page: a variable belongs to an environment or
/// to the project above it, and a secret is a variable whose value nobody may read back. Splitting
/// them would mean three round trips to answer "what does this environment actually resolve to".
/// </summary>
[Authorize]
[Route("projects/{projectId:guid}/environments")]
[ServiceFilter<WorkspaceContextFilter>]
public sealed class EnvironmentsController(
    ProofFlowDbContext db,
    ICurrentUser me,
    IAuditLog audit,
    ISecretCipher cipher,
    IStringLocalizer localizer) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Policies.ViewProject)]
    public async Task<IActionResult> Index(Guid projectId, Guid? selected, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        var environments = await db.Environments
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(cancellationToken);

        // Whatever was asked for, else the first — a page that opens on nothing makes the reader
        // click before they can see anything.
        var current = environments.FirstOrDefault(e => e.Id == selected) ?? environments.FirstOrDefault();

        var model = await BuildPageAsync(project.Id, project.Name, environments, current, cancellationToken);

        ViewData["Title"] = localizer["nav.environments"].Value;
        ViewData["Breadcrumbs"] = new List<(string, string?)>
        {
            (localizer["project.title"].Value, "/projects"),
            (project.Name, $"/projects/{project.Id}"),
            (localizer["nav.environments"].Value, null),
        };

        return View(model);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Create(Guid projectId, EnvironmentFormViewModel form, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null) return NotFound();

        if (!ModelState.IsValid) return await BackToIndexAsync(projectId, null, cancellationToken);

        var taken = await db.Environments
            .Where(e => e.ProjectId == projectId).Select(e => e.Slug).ToListAsync(cancellationToken);

        var environment = new ProjectEnvironment
        {
            WorkspaceId = me.WorkspaceId!.Value,
            ProjectId = projectId,
            Name = form.Name.Trim(),
            Slug = Slug.Unique(Slug.From(form.Name, "environment"), taken),
            SortOrder = taken.Count,
        };

        Apply(form, environment);
        db.Environments.Add(environment);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "environment.created", projectId, nameof(ProjectEnvironment), environment.Id, environment.Name,
            Details(environment)), cancellationToken);

        TempData.Success(localizer["environment.created", environment.Name]);
        return Redirect($"/projects/{projectId}/environments?selected={environment.Id}");
    }

    [HttpPost("{environmentId:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Update(
        Guid projectId, Guid environmentId, EnvironmentFormViewModel form, CancellationToken cancellationToken)
    {
        var environment = await db.Environments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, cancellationToken);
        if (environment is null) return NotFound();

        if (!ModelState.IsValid) return await BackToIndexAsync(projectId, environmentId, cancellationToken);

        // Recorded before the change, so the audit entry can say what was switched on rather than
        // only that something was. Turning on private-network reach is the entry that matters.
        var before = Details(environment);

        environment.Name = form.Name.Trim();
        Apply(form, environment);
        await db.SaveChangesAsync(cancellationToken);

        var after = Details(environment);
        var changed = after.Where(pair => before.GetValueOrDefault(pair.Key) != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        await audit.RecordAsync(new AuditEntry(
            "environment.updated", projectId, nameof(ProjectEnvironment), environment.Id, environment.Name,
            changed.Count > 0 ? changed : null), cancellationToken);

        TempData.Success(localizer["project.updated"]);
        return Redirect($"/projects/{projectId}/environments?selected={environment.Id}");
    }

    [HttpPost("{environmentId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> Delete(Guid projectId, Guid environmentId, CancellationToken cancellationToken)
    {
        var environment = await db.Environments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, cancellationToken);
        if (environment is null) return NotFound();

        // The secrets go with it. They are scoped to this environment and would otherwise be rows
        // nothing can reach and nobody can decrypt the purpose of.
        var secrets = await db.Secrets.Where(s => s.EnvironmentId == environmentId).ToListAsync(cancellationToken);
        var variables = await db.Variables.Where(v => v.EnvironmentId == environmentId).ToListAsync(cancellationToken);

        db.Secrets.RemoveRange(secrets);
        db.Variables.RemoveRange(variables);
        db.Environments.Remove(environment);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "environment.deleted", projectId, nameof(ProjectEnvironment), environmentId, environment.Name,
            new Dictionary<string, string?>
            {
                ["secrets"] = secrets.Count.ToString(),
                ["variables"] = variables.Count.ToString(),
            }), cancellationToken);

        TempData.Success(localizer["environment.deleted", environment.Name]);
        return Redirect($"/projects/{projectId}/environments");
    }

    // ---- variables --------------------------------------------------------------------------

    [HttpPost("variables")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> SaveVariable(
        Guid projectId, VariableFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData.Error(FirstError());
            return Redirect(Back(projectId, form.EnvironmentId));
        }

        var name = form.Name.Trim();

        var existing = await db.Variables.FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.EnvironmentId == form.EnvironmentId && v.Name == name,
            cancellationToken);

        if (existing is null)
        {
            db.Variables.Add(new EnvironmentVariable
            {
                WorkspaceId = me.WorkspaceId!.Value,
                ProjectId = projectId,
                EnvironmentId = form.EnvironmentId,
                Name = name,
                Value = form.Value,
                Description = form.Description,
            });
        }
        else
        {
            existing.Value = form.Value;
            existing.Description = form.Description;
        }

        await db.SaveChangesAsync(cancellationToken);

        // The name only. A variable's value is not a secret, but it is often a URL or an identifier
        // from a customer's system, and an audit log is read by more people than the project is.
        await audit.RecordAsync(new AuditEntry(
            "variable.saved", projectId, nameof(EnvironmentVariable), existing?.Id, name), cancellationToken);

        TempData.Success(localizer["variable.saved", name]);
        return Redirect(Back(projectId, form.EnvironmentId));
    }

    [HttpPost("variables/{variableId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageEnvironment)]
    public async Task<IActionResult> DeleteVariable(
        Guid projectId, Guid variableId, Guid? selected, CancellationToken cancellationToken)
    {
        var variable = await db.Variables
            .FirstOrDefaultAsync(v => v.Id == variableId && v.ProjectId == projectId, cancellationToken);
        if (variable is null) return NotFound();

        db.Variables.Remove(variable);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "variable.deleted", projectId, nameof(EnvironmentVariable), variableId, variable.Name), cancellationToken);

        return Redirect(Back(projectId, selected));
    }

    // ---- secrets ----------------------------------------------------------------------------

    [HttpPost("secrets")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageSecret)]
    public async Task<IActionResult> SaveSecret(
        Guid projectId, SecretFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData.Error(FirstError());
            return Redirect(Back(projectId, form.EnvironmentId));
        }

        var name = form.Name.Trim();
        var sealedValue = cipher.Seal(form.Value);

        var existing = await db.Secrets.FirstOrDefaultAsync(
            s => s.ProjectId == projectId && s.EnvironmentId == form.EnvironmentId && s.Name == name,
            cancellationToken);

        if (existing is null)
        {
            db.Secrets.Add(new Secret
            {
                WorkspaceId = me.WorkspaceId!.Value,
                ProjectId = projectId,
                EnvironmentId = form.EnvironmentId,
                Name = name,
                Description = form.Description,
                Ciphertext = sealedValue.Ciphertext,
                Nonce = sealedValue.Nonce,
                Tag = sealedValue.Tag,
                KeyVersion = sealedValue.KeyVersion,
                Preview = Preview(form.Value),
                CreatedByUserId = me.UserId,
            });
        }
        else
        {
            existing.Ciphertext = sealedValue.Ciphertext;
            existing.Nonce = sealedValue.Nonce;
            existing.Tag = sealedValue.Tag;
            existing.KeyVersion = sealedValue.KeyVersion;
            existing.Preview = Preview(form.Value);
            existing.Description = form.Description;
        }

        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            existing is null ? "secret.created" : "secret.updated",
            projectId, nameof(Secret), existing?.Id, name), cancellationToken);

        TempData.Success(localizer["secret.saved", name]);
        return Redirect(Back(projectId, form.EnvironmentId));
    }

    [HttpPost("secrets/{secretId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageSecret)]
    public async Task<IActionResult> DeleteSecret(
        Guid projectId, Guid secretId, Guid? selected, CancellationToken cancellationToken)
    {
        var secret = await db.Secrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.ProjectId == projectId, cancellationToken);
        if (secret is null) return NotFound();

        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(
            "secret.deleted", projectId, nameof(Secret), secretId, secret.Name), cancellationToken);

        TempData.Success(localizer["secret.deleted", secret.Name]);
        return Redirect(Back(projectId, selected));
    }

    /// <summary>
    /// Hands back one secret's plaintext, once.
    ///
    /// The only path in the application that decrypts for a person rather than for a request, so it
    /// is the narrowest: a capability almost nobody holds, a POST so it cannot be linked to or
    /// prefetched, an audit entry every single time, and one secret per call. The browser hides it
    /// again after thirty seconds — which is a courtesy against a shoulder, not a security control,
    /// and is not pretended to be one.
    /// </summary>
    [HttpPost("secrets/{secretId:guid}/reveal")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ViewSecret)]
    public async Task<IActionResult> RevealSecret(Guid projectId, Guid secretId, CancellationToken cancellationToken)
    {
        var secret = await db.Secrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.ProjectId == projectId, cancellationToken);
        if (secret is null) return NotFound();

        // Written before the value is handed over, so a read is on the record even if the response
        // never arrives.
        await audit.RecordAsync(new AuditEntry(
            "secret.revealed", projectId, nameof(Secret), secret.Id, secret.Name), cancellationToken);

        try
        {
            var value = cipher.Open(new SealedSecret(
                secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion));

            return Json(new { value });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: localizer["secret.unreadable"].Value,
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    // ---- assembly ---------------------------------------------------------------------------

    private async Task<EnvironmentsPageViewModel> BuildPageAsync(
        Guid projectId, string projectName, List<ProjectEnvironment> environments,
        ProjectEnvironment? current, CancellationToken cancellationToken)
    {
        var variableCounts = await db.Variables
            .Where(v => v.ProjectId == projectId)
            .GroupBy(v => v.EnvironmentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var secretCounts = await db.Secrets
            .Where(s => s.ProjectId == projectId)
            .GroupBy(s => s.EnvironmentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var summaries = environments.Select(e => new EnvironmentSummary(
            e.Id, e.Name, e.Slug, e.BaseUrl, e.Kind, e.IsProduction,
            e.AllowPrivateNetwork, e.AllowInvalidCertificate,
            variableCounts.FirstOrDefault(c => c.Key == e.Id)?.Count ?? 0,
            secretCounts.FirstOrDefault(c => c.Key == e.Id)?.Count ?? 0)).ToList();

        var variables = current is null ? [] : await db.Variables
            .Where(v => v.ProjectId == projectId && (v.EnvironmentId == null || v.EnvironmentId == current.Id))
            .OrderBy(v => v.EnvironmentId == null ? 0 : 1).ThenBy(v => v.Name)
            .Select(v => new VariableRow(v.Id, v.Name, v.Value, v.Description, v.EnvironmentId == null))
            .ToListAsync(cancellationToken);

        var secrets = current is null ? [] : await db.Secrets
            .Where(s => s.ProjectId == projectId && (s.EnvironmentId == null || s.EnvironmentId == current.Id))
            .OrderBy(s => s.EnvironmentId == null ? 0 : 1).ThenBy(s => s.Name)
            .Select(s => new SecretRow(
                s.Id, s.Name, s.Description, s.Preview, s.EnvironmentId == null, s.LastUsedAt))
            .ToListAsync(cancellationToken);

        return new EnvironmentsPageViewModel
        {
            ProjectId = projectId,
            ProjectName = projectName,
            Environments = summaries,
            Selected = current is null ? null : ToForm(current),
            Variables = variables,
            Secrets = secrets,
            CanManage = me.Can(Capability.ManageEnvironment),
            CanManageSecrets = me.Can(Capability.ManageSecret),
            CanRevealSecrets = me.Can(Capability.ViewSecret),
        };
    }

    private async Task<IActionResult> BackToIndexAsync(
        Guid projectId, Guid? selected, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var environments = await db.Environments
            .Where(e => e.ProjectId == projectId).OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(cancellationToken);

        var current = environments.FirstOrDefault(e => e.Id == selected) ?? environments.FirstOrDefault();
        ViewData["Title"] = localizer["nav.environments"].Value;

        return View("Index", await BuildPageAsync(projectId, project.Name, environments, current, cancellationToken));
    }

    private static void Apply(EnvironmentFormViewModel form, ProjectEnvironment environment)
    {
        environment.BaseUrl = string.IsNullOrWhiteSpace(form.BaseUrl) ? null : form.BaseUrl.Trim().TrimEnd('/');
        environment.Kind = form.Kind;
        environment.TimeoutSeconds = form.TimeoutSeconds;
        environment.MaxRedirects = form.MaxRedirects;
        environment.MaxResponseKilobytes = form.MaxResponseKilobytes;
        environment.AllowedHosts = string.IsNullOrWhiteSpace(form.AllowedHosts) ? null : form.AllowedHosts.Trim();
        environment.AllowPrivateNetwork = form.AllowPrivateNetwork;
        environment.AllowInvalidCertificate = form.AllowInvalidCertificate;
        environment.ProxyUrl = string.IsNullOrWhiteSpace(form.ProxyUrl) ? null : form.ProxyUrl.Trim();

        // Choosing the Production kind is the same statement as ticking the box, and a form where
        // the two can disagree produces an environment that looks safe in the list and is not.
        environment.IsProduction = form.IsProduction || form.Kind == EnvironmentKind.Production;
    }

    private static EnvironmentFormViewModel ToForm(ProjectEnvironment e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        BaseUrl = e.BaseUrl,
        Kind = e.Kind,
        TimeoutSeconds = e.TimeoutSeconds,
        MaxRedirects = e.MaxRedirects,
        MaxResponseKilobytes = e.MaxResponseKilobytes,
        AllowedHosts = e.AllowedHosts,
        AllowPrivateNetwork = e.AllowPrivateNetwork,
        AllowInvalidCertificate = e.AllowInvalidCertificate,
        IsProduction = e.IsProduction,
        ProxyUrl = e.ProxyUrl,
    };

    /// <summary>The settings worth having in the audit trail, as strings.</summary>
    private static Dictionary<string, string?> Details(ProjectEnvironment e) => new()
    {
        ["baseUrl"] = e.BaseUrl,
        ["kind"] = e.Kind.ToString(),
        ["allowPrivateNetwork"] = e.AllowPrivateNetwork ? "true" : "false",
        ["allowInvalidCertificate"] = e.AllowInvalidCertificate ? "true" : "false",
        ["isProduction"] = e.IsProduction ? "true" : "false",
        ["allowedHosts"] = e.AllowedHosts,
    };

    /// <summary>
    /// The last four characters, and only when there are enough of them to spare.
    ///
    /// Below eight characters the last four is most of the secret, so short values show nothing —
    /// the preview exists to tell two keys apart, not to be a hint.
    /// </summary>
    private static string Preview(string value) =>
        value.Length >= 8 ? value[^4..] : string.Empty;

    private static string Back(Guid projectId, Guid? selected) =>
        selected is null
            ? $"/projects/{projectId}/environments"
            : $"/projects/{projectId}/environments?selected={selected}";

    private string FirstError() =>
        ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
        ?? localizer["error.body"].Value;
}
