using System.Text.Json;
using FluentAssertions;

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
