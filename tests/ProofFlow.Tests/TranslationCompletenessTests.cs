using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ProofFlow.Domain.Baselines;
using ProofFlow.Domain.Capture;
using ProofFlow.TestEngine.Comparison;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Tests;

/// <summary>
/// Persian must say everything English says.
///
/// This is the test that stops the specific failure the brief calls out: a hundred English
/// sentences in the middle of a Persian panel. A missing key does not throw, does not log at any
/// level anyone watches, and renders as perfectly valid English — so nothing except a build-time
/// set difference catches it.
/// </summary>
public class TranslationCompletenessTests
{
    private static readonly string ResourcesDirectory = LocateResources();

    [Fact]
    public void Persian_has_every_key_English_has()
    {
        var english = Flatten("en.json");
        var persian = Flatten("fa.json");

        var missing = english.Keys.Except(persian.Keys).OrderBy(k => k).ToArray();

        missing.Should().BeEmpty(
            "every English string needs a Persian one; missing: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void Persian_has_no_keys_English_lacks()
    {
        var english = Flatten("en.json");
        var persian = Flatten("fa.json");

        // A Persian-only key is a key nothing reads: the code looks up names that exist in the
        // neutral catalogue, so this is always either a typo or a leftover.
        var orphans = persian.Keys.Except(english.Keys).OrderBy(k => k).ToArray();

        orphans.Should().BeEmpty("orphaned Persian keys: {0}", string.Join(", ", orphans));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var english = Flatten("en.json");
        var persian = Flatten("fa.json");

        var mismatched = new List<string>();

        foreach (var (key, englishValue) in english)
        {
            if (!persian.TryGetValue(key, out var persianValue)) continue;

            // A translation that drops {0} silently loses the project name it was supposed to
            // carry; one that invents {1} throws FormatException at render time, in production,
            // in the language nobody on the team reads first.
            var expected = Placeholders(englishValue);
            var actual = Placeholders(persianValue);

            if (!expected.SetEquals(actual)) mismatched.Add(key);
        }

        mismatched.Should().BeEmpty("placeholders differ in: {0}", string.Join(", ", mismatched));
    }

    [Fact]
    public void No_string_is_empty()
    {
        foreach (var file in new[] { "en.json", "fa.json" })
        {
            var blank = Flatten(file).Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key).ToArray();

            blank.Should().BeEmpty("{0} has blank values: {1}", file, string.Join(", ", blank));
        }
    }

    /// <summary>
    /// Every key the markup asks for has to exist.
    ///
    /// The failure this catches is not a crash. A missing key renders as itself, so the page shows
    /// "environment.title" in a table heading and goes on working — and it survives review because
    /// it looks like a variable name somebody expects to see somewhere. It reached a screenshot
    /// before anything noticed.
    ///
    /// Only literal keys are found this way. The ones this project builds from an enum name are
    /// covered by the theory below rather than quietly skipped.
    /// </summary>
    [Fact]
    public void Every_key_the_markup_asks_for_exists()
    {
        var english = Flatten("en.json");
        var web = Path.GetDirectoryName(ResourcesDirectory)!;

        var referenced = new SortedSet<string>(StringComparer.Ordinal);

        void Scan(string file, string pattern)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), pattern))
            {
                referenced.Add(match.Groups[1].Value);
            }
        }

        foreach (var file in Directory.EnumerateFiles(web, "*.cshtml", SearchOption.AllDirectories))
        {
            Scan(file, "T\\[\"([a-zA-Z][a-zA-Z0-9._]*)\"");
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(web, "Scripts"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".vue") || f.EndsWith(".ts")))
        {
            Scan(file, @"\bt\('([a-zA-Z][a-zA-Z0-9._]*)'");
        }

        referenced.Should().NotBeEmpty("the scan should find keys at all");

        var missing = referenced.Where(key => !english.ContainsKey(key)).ToArray();

        missing.Should().BeEmpty(
            "these keys are used in markup but are not in en.json: {0}", string.Join(", ", missing));
    }

    /// <summary>
    /// Every action the code records has a sentence to render it with.
    ///
    /// The audit log looks its labels up by the dotted key the call site wrote, and an unknown key
    /// renders as itself — so a whole phase's worth of events can arrive in the log reading
    /// "audit.action.team.roleChanged" while every test stays green. That is exactly what happened:
    /// the schedule, matrix, run, API-key and team events were all recorded for several phases
    /// before anybody noticed none of them had a label.
    ///
    /// Scanned from the source rather than kept as a list, because a list is a second place to
    /// forget.
    /// </summary>
    [Fact]
    public void Every_recorded_action_has_a_label()
    {
        var english = Flatten("en.json");
        var (recorded, _) = RecordedActions();

        recorded.Should().NotBeEmpty("the scan should find recorded actions at all");

        var missing = recorded.Where(action => !english.ContainsKey($"audit.action.{action}")).ToArray();

        missing.Should().BeEmpty(
            "these actions are recorded but the log has no sentence for them: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    /// And nothing the other way round.
    ///
    /// A label for an action nothing records is a label nobody will ever see and nobody will think
    /// to keep true — three <c>member.*</c> entries sat in both catalogues for eight phases while
    /// the code wrote <c>team.*</c>.
    /// </summary>
    [Fact]
    public void Every_label_belongs_to_an_action_something_records()
    {
        var english = Flatten("en.json");
        var (recorded, families) = RecordedActions();

        var orphans = english.Keys
            .Where(key => key.StartsWith("audit.action.", StringComparison.Ordinal))
            .Select(key => key["audit.action.".Length..])
            .Where(action => !recorded.Contains(action)
                             && !families.Any(prefix => action.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(action => action)
            .ToArray();

        orphans.Should().BeEmpty("nothing records these: {0}", string.Join(", ", orphans));
    }

    /// <summary>
    /// The one family built at run time, checked against the enum it is built from.
    ///
    /// <c>capture.{status}</c> is composed from the decision a reviewer made, so the scan above can
    /// only see the prefix. Adding a status to the enum is a one-line change that would otherwise
    /// leave an audit row reading "audit.action.capture.quarantined".
    /// </summary>
    [Fact]
    public void Every_review_decision_has_a_label()
    {
        var english = Flatten("en.json");

        var missing = Enum.GetNames<SampleStatus>()
            .Select(name => $"audit.action.capture.{name.ToLowerInvariant()}")
            .Where(key => !english.ContainsKey(key))
            .ToArray();

        // Not every status is one somebody chooses — Captured is what a sweep produces — so this
        // reports what is missing rather than demanding all of them.
        missing.Should().NotContain("audit.action.capture.approved");
        missing.Should().NotContain("audit.action.capture.rejected");
        missing.Should().NotContain("audit.action.capture.reviewed");
    }

    /// <summary>
    /// Every action key the source records, and the prefixes of the ones it composes.
    ///
    /// The first argument of <c>AuditEntry</c> is read rather than the whole call, so a ternary
    /// between two literals — which is how a create-or-update pair is written — yields both.
    /// </summary>
    private static (SortedSet<string> Literal, IReadOnlyList<string> Families) RecordedActions()
    {
        var source = Path.GetDirectoryName(Path.GetDirectoryName(ResourcesDirectory))!;

        var literal = new SortedSet<string>(StringComparer.Ordinal);
        var families = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);

            foreach (Match call in Regex.Matches(text, @"AuditEntry\(([^,)]*)"))
            {
                foreach (Match name in Regex.Matches(call.Groups[1].Value, @"""([a-z][a-zA-Z0-9]*\.[a-zA-Z0-9.]+)"""))
                {
                    literal.Add(name.Groups[1].Value);
                }
            }

            foreach (Match composed in Regex.Matches(text, @"AuditEntry\(\s*\$""([a-z][a-zA-Z0-9]*\.)\{"))
            {
                families.Add(composed.Groups[1].Value);
            }
        }

        return (literal, [.. families]);
    }

    /// <summary>
    /// Every icon the markup names is one the bundle actually carries.
    ///
    /// The registry is hand-written on purpose — importing lucide's barrel takes the bundle from
    /// about 140 kB to over 800 kB — and the cost of that decision is exactly this failure: an
    /// unregistered name renders as an empty box. No error, no warning, no missing element. The
    /// mail-check on the password-reset confirmation reached a screenshot that way.
    /// </summary>
    [Fact]
    public void Every_icon_the_markup_names_is_in_the_bundle()
    {
        var web = Path.GetDirectoryName(ResourcesDirectory)!;
        var registry = File.ReadAllText(Path.Combine(web, "Scripts", "lib", "icons.ts"));

        var listed = registry[(registry.IndexOf("const used = {", StringComparison.Ordinal))..];
        listed = listed[..listed.IndexOf("};", StringComparison.Ordinal)];

        var registered = new HashSet<string>(
            Regex.Matches(listed, "[A-Z][A-Za-z0-9]*").Select(match => match.Value),
            StringComparer.Ordinal);

        var named = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cshtml") || path.EndsWith(".vue"))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, @"data-lucide=""([a-z0-9-]+)"""))
            {
                named.Add(match.Groups[1].Value);
            }

            // The Vue components ask for the same glyphs through a wrapper. Only the literal ones
            // are visible to a scan; the ones bound to a variable are covered by the pages that
            // render them.
            foreach (Match match in Regex.Matches(text, @"<Icon\s+name=""([a-z0-9-]+)"""))
            {
                named.Add(match.Groups[1].Value);
            }
        }

        named.Should().NotBeEmpty("the scan should find icon names at all");

        var missing = named
            .Where(name => !registered.Contains(
                string.Concat(name.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]))))
            .ToArray();

        missing.Should().BeEmpty(
            "these icons are named in markup but not imported in Scripts/lib/icons.ts, so they render "
            + "as empty boxes: {0}", string.Join(", ", missing));
    }

    /// <summary>
    /// The keys built at render time from an enum name, checked against the enums themselves.
    ///
    /// These are the ones the scan above cannot see, and also the ones most likely to drift:
    /// adding a matcher to the engine is a one-line change that silently leaves a dropdown entry
    /// reading "matcher.NewThing".
    /// </summary>
    [Theory]
    [InlineData("matcher.{0}", typeof(MatcherKind))]
    [InlineData("matcher.{0}.help", typeof(MatcherKind))]
    [InlineData("diff.kind.{0}", typeof(DiffKind))]
    [InlineData("dynamic.{0}", typeof(DynamicReason))]
    [InlineData("confidence.{0}", typeof(Confidence))]
    [InlineData("baseline.status.{0}", typeof(BaselineStatus))]
    public void Every_enum_member_has_a_string(string pattern, Type enumType)
    {
        var english = Flatten("en.json");

        var missing = Enum.GetNames(enumType)
            .Select(name => string.Format(pattern, name))
            .Where(key => !english.ContainsKey(key))
            .ToArray();

        missing.Should().BeEmpty("missing: {0}", string.Join(", ", missing));
    }

    /// <summary>
    /// Every node type, port, property and option the catalogue names has a string.
    ///
    /// Seventy node types is seventy titles, seventy summaries and some hundreds of labels between
    /// them, and none of it is written in the markup — the palette asks for
    /// <c>node.{key}.title</c> at render time, so a missing one is a palette entry reading
    /// "node.flow.pollUntil.title" and nothing else complaining.
    /// </summary>
    [Fact]
    public void Every_node_type_can_be_read()
    {
        var english = Flatten("en.json");
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        void Want(string key)
        {
            if (!english.ContainsKey(key)) missing.Add(key);
        }

        foreach (var spec in NodeCatalogue.All)
        {
            Want(spec.TitleKey);
            Want(spec.SummaryKey);

            foreach (var port in spec.Inputs.Concat(spec.Outputs))
            {
                Want(port.LabelKey);
                Want($"portType.{port.Type}");
            }

            foreach (var property in spec.Properties)
            {
                Want(property.LabelKey);
                if (property.HelpKey is { } help) Want(help);

                foreach (var option in property.Options) Want($"option.{option}");
            }
        }

        foreach (var group in Enum.GetNames<NodeGroup>()) Want($"nodeGroup.{group}");

        missing.Should().BeEmpty("the canvas renders these keys at run time: {0}",
            string.Join(", ", missing));
    }

    private static HashSet<string> Placeholders(string value)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(value, @"\{(\d+)\}"))
        {
            found.Add(match.Groups[1].Value);
        }
        return found;
    }

    private static Dictionary<string, string> Flatten(string fileName)
    {
        var path = Path.Combine(ResourcesDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{path} should exist");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(document.RootElement, string.Empty, flat);
        return flat;
    }

    private static void Walk(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", into);
                break;

            case JsonValueKind.String:
                into[prefix] = element.GetString() ?? string.Empty;
                break;
        }
    }

    /// <summary>
    /// Walks up from the test binary to the repository root.
    ///
    /// The resource files are content of the web project, not of this one, so there is no copy of
    /// them beside the test assembly — and reading the shipped copy is the point: this test must
    /// fail when the file a translator edited is wrong, not when a stale copy is.
    /// </summary>
    private static string LocateResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ProofFlow.Web", "Resources");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/ProofFlow.Web/Resources was not found above the test output.");
    }
}
