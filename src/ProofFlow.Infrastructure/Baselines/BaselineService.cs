using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Baselines;
using ProofFlow.Domain.Baselines;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.TestEngine.Comparison;

namespace ProofFlow.Infrastructure.Baselines;

/// <summary>
/// Capturing a baseline, comparing against it, and moving it to the next version.
///
/// The lifecycle rules live here rather than in a controller because they are the product's
/// promises, not its plumbing: a version is never edited once approved, accepting a change creates
/// the next one, and the rules in force at the time are frozen into the version so a comparison
/// from March can still say what it actually compared.
/// </summary>
public sealed class BaselineService(ProofFlowDbContext db, ICurrentUser me, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Stores a response as the first version of a new baseline, or as the next draft of one that
    /// already exists.
    /// </summary>
    public async Task<BaselineVersion> CaptureAsync(
        Baseline baseline, string body, string? contentType, int statusCode,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        var next = await db.BaselineVersions
            .Where(v => v.BaselineId == baseline.Id)
            .Select(v => v.Number)
            .ToListAsync(cancellationToken);

        var rules = await LoadRulesAsync(baseline.Id, cancellationToken);

        var version = new BaselineVersion
        {
            WorkspaceId = baseline.WorkspaceId,
            BaselineId = baseline.Id,
            Number = next.Count == 0 ? 1 : next.Max() + 1,
            Status = BaselineStatus.Draft,
            Body = body,
            ContentType = contentType,
            StatusCode = statusCode,
            HeadersJson = headers is null ? null : JsonSerializer.Serialize(headers, Json),
            // Frozen at capture. Rules change; a version that pointed at the live set could not
            // say what it meant at the time, which is the whole reason to keep versions.
            RulesJson = JsonSerializer.Serialize(rules.Select(ToDto), Json),
            NormalizedHash = Hash(body, new ComparisonRuleSet(rules)),
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.BaselineVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        return version;
    }

    /// <summary>
    /// Compares a fresh response against the approved version, under the baseline's current rules.
    /// </summary>
    public async Task<DiffResultDto> CompareAsync(
        Baseline baseline, string body, int statusCode, double durationMs,
        CancellationToken cancellationToken = default)
    {
        var approved = await ApprovedVersionAsync(baseline.Id, cancellationToken);

        if (approved is null)
        {
            return new DiffResultDto
            {
                Matches = false,
                Rows = [],
                Counts = new Dictionary<string, int>(),
                FindingIndexes = [],
                FailureMessage = "This baseline has no approved version to compare against yet.",
                StatusCode = statusCode,
                DurationMs = durationMs,
            };
        }

        var rules = await LoadRulesAsync(baseline.Id, cancellationToken);
        var ruleSet = new ComparisonRuleSet(rules);

        var diff = SemanticDiff.CompareText(approved.Body, body, ruleSet);

        return Flatten(diff, $"v{approved.Number}", statusCode, durationMs);
    }

    /// <summary>
    /// Turns the engine's tree into the flat list the viewer renders.
    ///
    /// Depth-first, so the order is the document's order and stepping with n and p walks the
    /// response the way a person reads it. Rows that are neither findings nor on the path to one
    /// are kept — a diff that only shows what changed gives no sense of what did not.
    /// </summary>
    public static DiffResultDto Flatten(DiffResult diff, string version, int statusCode, double durationMs)
    {
        var rows = new List<DiffRowDto>();
        var findings = new List<int>();

        void Visit(DiffNode node, int depth)
        {
            var index = rows.Count;

            rows.Add(new DiffRowDto
            {
                Index = index,
                Path = node.Path,
                Leaf = node.Location.Leaf,
                Depth = depth,
                Kind = node.Kind.ToString(),
                Expected = node.Expected,
                Actual = node.Actual,
                Reason = node.Reason,
                RulePath = node.RulePath,
                RuleKind = node.RuleKind?.ToString(),
                HasChildren = node.Children.Count > 0,
                HasFindings = node.HasFindings,
            });

            if (node.Kind is not (DiffKind.Unchanged or DiffKind.Ignored) && node.Children.Count == 0)
            {
                findings.Add(index);
            }

            foreach (var child in node.Children) Visit(child, depth + 1);
        }

        Visit(diff.Root, 0);

        return new DiffResultDto
        {
            Matches = diff.Matches,
            Rows = rows,
            Counts = diff.Counts.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            FindingIndexes = findings,
            InvalidRules = [.. diff.InvalidRules.Select(rule => rule.Path)],
            BaselineVersion = version,
            StatusCode = statusCode,
            DurationMs = durationMs,
        };
    }

    /// <summary>
    /// Accepts some of a response's changes into a new version, rejecting the rest.
    ///
    /// Partial acceptance is the point. A response with a legitimately updated price and an
    /// accidentally dropped field is the ordinary case, and a baseline that can only be accepted
    /// whole forces somebody to choose between blessing a defect and losing a real change.
    /// </summary>
    public async Task<BaselineVersion> AcceptAsync(
        Baseline baseline, string responseBody, string? contentType, int statusCode,
        AcceptChangesCommand command, CancellationToken cancellationToken = default)
    {
        var approved = await ApprovedVersionAsync(baseline.Id, cancellationToken)
            ?? throw new InvalidOperationException("There is no approved version to build on.");

        foreach (var rule in command.NewRules)
        {
            db.BaselineRules.Add(new BaselineRule
            {
                WorkspaceId = baseline.WorkspaceId,
                BaselineId = baseline.Id,
                Path = rule.Path,
                Matcher = rule.Matcher,
                Text = rule.Text,
                Number = rule.Number,
                Number2 = rule.Number2,
                Note = rule.Note,
                Enabled = rule.Enabled,
                FromSuggestion = true,
            });
        }

        var merged = Merge(approved.Body, responseBody, command.AcceptedPaths);
        await db.SaveChangesAsync(cancellationToken);

        var version = await CaptureAsync(
            baseline, merged, contentType, statusCode, headers: null, cancellationToken);

        version.Description = command.Description;
        version.SupersededVersionId = approved.Id;
        version.Status = BaselineStatus.PendingApproval;

        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    /// <summary>
    /// Builds the next body: the approved one, with only the accepted paths taken from the response.
    ///
    /// Written by walking the accepted paths rather than by taking the new response wholesale,
    /// because "accept these three fields" has to mean those three and not "and whatever else
    /// happened to change in the same call".
    /// </summary>
    private static string Merge(string approvedBody, string responseBody, IReadOnlyList<string> acceptedPaths)
    {
        if (acceptedPaths.Count == 0) return approvedBody;

        JsonNode? target, source;
        try
        {
            target = JsonNode.Parse(approvedBody);
            source = JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            // Not JSON: the only meaningful acceptance is the whole thing.
            return responseBody;
        }

        if (target is null || source is null) return responseBody;

        foreach (var path in acceptedPaths)
        {
            if (!PathPattern.TryParse(path, out _)) continue;
            Apply(target, source, path);
        }

        return target.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Copies one concrete path from the response into the baseline body.
    ///
    /// Only concrete paths — a wildcard would mean accepting places nobody looked at, and the
    /// interface only ever offers paths that came from an actual differing row.
    /// </summary>
    private static void Apply(JsonNode target, JsonNode source, string path)
    {
        var steps = Steps(path);
        if (steps.Count == 0) return;

        var parentOfTarget = Navigate(target, steps[..^1]);
        var wanted = Navigate(source, steps);
        var last = steps[^1];

        switch (parentOfTarget)
        {
            case JsonObject obj when last.Name is { } name:
                // A null wanted value means the field is gone in the response, which is what
                // accepting a removal means.
                if (wanted is null) obj.Remove(name);
                else obj[name] = wanted.DeepClone();
                break;

            case JsonArray array when last.Index is { } index && index >= 0 && index < array.Count:
                if (wanted is not null) array[index] = wanted.DeepClone();
                break;

            case JsonArray array when last.Index is { } append && append == array.Count && wanted is not null:
                array.Add(wanted.DeepClone());
                break;
        }
    }

    private static JsonNode? Navigate(JsonNode? node, IReadOnlyList<Step> steps)
    {
        foreach (var step in steps)
        {
            node = step.Name is { } name
                ? node is JsonObject obj && obj.TryGetPropertyValue(name, out var child) ? child : null
                : node is JsonArray array && step.Index is { } i && i >= 0 && i < array.Count ? array[i] : null;

            if (node is null) return null;
        }

        return node;
    }

    /// <summary>Parses a concrete path back into steps. Mirrors <see cref="JsonLocation"/>'s output.</summary>
    private static List<Step> Steps(string path)
    {
        var steps = new List<Step>();
        var text = path.StartsWith('$') ? path[1..] : path;
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '.') { index++; continue; }

            if (text[index] == '[')
            {
                var close = text.IndexOf(']', index);
                if (close < 0) return [];

                var inside = text[(index + 1)..close].Trim();
                steps.Add(int.TryParse(inside, out var position)
                    ? Step.At(position)
                    : Step.Field(inside.Trim('\'', '"')));

                index = close + 1;
                continue;
            }

            var next = text.IndexOfAny(['.', '['], index);
            var name = next < 0 ? text[index..] : text[index..next];
            index = next < 0 ? text.Length : next;

            if (name.Length > 0) steps.Add(Step.Field(name));
        }

        return steps;
    }

    /// <summary>
    /// Approves a version and retires the one it replaced.
    ///
    /// The approver is recorded separately from the author because the separation is the point of
    /// having a review at all.
    /// </summary>
    public async Task ApproveAsync(BaselineVersion version, CancellationToken cancellationToken = default)
    {
        var baseline = await db.Baselines.FirstAsync(b => b.Id == version.BaselineId, cancellationToken);

        var current = await ApprovedVersionAsync(baseline.Id, cancellationToken);
        if (current is not null && current.Id != version.Id) current.Status = BaselineStatus.Superseded;

        version.Status = BaselineStatus.Approved;
        version.ApprovedByUserId = me.UserId;
        version.ApprovedAt = clock.UtcNow;

        baseline.ApprovedVersionId = version.Id;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectAsync(
        BaselineVersion version, string? reason, CancellationToken cancellationToken = default)
    {
        version.Status = BaselineStatus.Rejected;
        version.RejectionReason = reason;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<BaselineVersion?> ApprovedVersionAsync(Guid baselineId, CancellationToken cancellationToken) =>
        db.BaselineVersions
            .Where(v => v.BaselineId == baselineId && v.Status == BaselineStatus.Approved)
            .OrderByDescending(v => v.Number)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<List<ComparisonRule>> LoadRulesAsync(Guid baselineId, CancellationToken cancellationToken)
    {
        var rows = await db.BaselineRules
            .Where(r => r.BaselineId == baselineId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new ComparisonRule
        {
            Path = row.Path,
            // Stored by name so renumbering the engine's enum cannot silently change what a saved
            // rule means. An unknown name falls back to Exact, which is the strict reading.
            Kind = Enum.TryParse<MatcherKind>(row.Matcher, ignoreCase: true, out var kind) ? kind : MatcherKind.Exact,
            Text = row.Text,
            Number = row.Number,
            Number2 = row.Number2,
            Note = row.Note,
            Enabled = row.Enabled,
        })];
    }

    /// <summary>
    /// Suggestions for a body, minus anything a rule already covers.
    ///
    /// Offering a field that is already ignored is noise, and noise in this list is what makes
    /// people tick everything without reading.
    /// </summary>
    public async Task<IReadOnlyList<SuggestionDto>> SuggestAsync(
        Guid baselineId, string body, CancellationToken cancellationToken)
    {
        var ruleSet = new ComparisonRuleSet(await LoadRulesAsync(baselineId, cancellationToken));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        return
        [
            .. DynamicFieldDetector.Suggest(node)
                .Where(suggestion => !Covered(ruleSet, suggestion.Path))
                .Select(suggestion => new SuggestionDto(
                    suggestion.Path,
                    suggestion.Reason.ToString(),
                    suggestion.Confidence.ToString(),
                    suggestion.Rule.Kind.ToString(),
                    suggestion.Rule.Note,
                    suggestion.Sample)),
        ];
    }

    private static bool Covered(ComparisonRuleSet rules, string path)
    {
        var steps = Steps(path);
        var location = JsonLocation.Root;

        foreach (var step in steps)
        {
            location = step.Name is { } name ? location.Field(name) : location.At(step.Index!.Value);
        }

        return rules.For(location) is not null;
    }

    private static RuleDto ToDto(ComparisonRule rule) => new()
    {
        Path = rule.Path,
        Matcher = rule.Kind.ToString(),
        Text = rule.Text,
        Number = rule.Number,
        Number2 = rule.Number2,
        Note = rule.Note,
        Enabled = rule.Enabled,
    };

    /// <summary>
    /// A fingerprint of the body after the rules have had their say.
    ///
    /// Lets a replay answer "did anything change?" without a full comparison, which matters when a
    /// suite walks two thousand samples. Only a mismatch triggers the real diff.
    /// </summary>
    public static string Hash(string body, ComparisonRuleSet rules)
    {
        // Compared against itself under the same rules: whatever the rules set aside cannot
        // contribute, so two bodies differing only in ignored fields hash the same.
        var diff = SemanticDiff.CompareText(body, body, rules);
        var canonical = new StringBuilder();

        void Visit(DiffNode node)
        {
            if (node.Kind == DiffKind.Ignored) return;
            if (node.Children.Count == 0) canonical.Append(node.Path).Append('=').Append(node.Actual).Append('\n');
            foreach (var child in node.Children) Visit(child);
        }

        Visit(diff.Root);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
