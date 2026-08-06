using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace ProofFlow.Web.Infrastructure.Localization;

/// <summary>
/// Translations, from JSON files rather than .resx.
///
/// Three reasons this is worth departing from the framework default for.
///
/// The interface is half Vue. Section 8 of the brief forbids hard-coded strings inside components,
/// which means every island needs a dictionary handed to it — and a resx has to be flattened into
/// JSON at some point anyway. Starting as JSON removes the conversion and the chance of the two
/// drifting.
///
/// A .resx pairs a key with one language's text, so "which keys is Persian missing?" is a question
/// you can only answer by opening both files in a designer. Here it is a set difference, and
/// <c>TranslationCompletenessTests</c> asks it on every build.
///
/// And translators can edit JSON. A resx is XML with a schema and a designer.
/// </summary>
public sealed class JsonTranslations
{
    private readonly string _directory;
    private readonly ILogger<JsonTranslations> _logger;
    private readonly bool _reloadOnChange;
    private readonly ConcurrentDictionary<string, MissingKey> _missing = new();

    private Dictionary<string, Dictionary<string, string>> _catalogues = [];
    private DateTimeOffset _loadedAt;

    /// <summary>
    /// The language every key is guaranteed to have. A lookup that finds nothing anywhere returns
    /// the key itself, which is ugly on screen — deliberately, because an ugly string gets
    /// reported and a plausible-looking English fallback in a Persian page does not.
    /// </summary>
    public const string NeutralCulture = "en";

    public JsonTranslations(IWebHostEnvironment environment, ILogger<JsonTranslations> logger)
    {
        _directory = Path.Combine(environment.ContentRootPath, "Resources");
        _logger = logger;
        _reloadOnChange = environment.IsDevelopment();
        Load();
    }

    public IReadOnlyCollection<string> Cultures => _catalogues.Keys;

    /// <summary>Every key/value pair for one culture, already merged over the neutral language.</summary>
    public IReadOnlyDictionary<string, string> Catalogue(string culture)
    {
        ReloadIfStale();

        var merged = new Dictionary<string, string>(Neutral(), StringComparer.Ordinal);
        if (culture != NeutralCulture && _catalogues.TryGetValue(Normalise(culture), out var specific))
        {
            foreach (var (key, value) in specific) merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// Every key under one prefix, with the prefix stripped — what an island is handed.
    ///
    /// <c>Subset("canvas.")</c> gives a component the fifteen strings it needs instead of the
    /// eleven hundred the application has, which keeps the page's inline JSON small.
    /// </summary>
    public Dictionary<string, string> Subset(string culture, string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Catalogue(culture))
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                result[key[prefix.Length..]] = value;
        }
        return result;
    }

    public bool TryGet(string culture, string key, out string value)
    {
        ReloadIfStale();

        var normalised = Normalise(culture);

        if (_catalogues.TryGetValue(normalised, out var specific) && specific.TryGetValue(key, out var hit))
        {
            value = hit;
            return true;
        }

        if (Neutral().TryGetValue(key, out var neutral))
        {
            // Found in English but not in the requested language: that is the exact failure this
            // class exists to make visible, so record it rather than quietly serving English.
            if (normalised != NeutralCulture) RecordMissing(normalised, key);
            value = neutral;
            return true;
        }

        RecordMissing(normalised, key);
        value = key;
        return false;
    }

    /// <summary>Keys asked for that no catalogue had. Surfaced on the diagnostics page.</summary>
    public IReadOnlyCollection<MissingKey> Missing => _missing.Values.ToArray();

    private Dictionary<string, string> Neutral() =>
        _catalogues.TryGetValue(NeutralCulture, out var neutral) ? neutral : [];

    private void RecordMissing(string culture, string key) =>
        _missing.TryAdd($"{culture}:{key}", new MissingKey(culture, key));

    private static string Normalise(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return NeutralCulture;
        var dash = culture.IndexOf('-');
        return (dash > 0 ? culture[..dash] : culture).ToLowerInvariant();
    }

    private void ReloadIfStale()
    {
        if (!_reloadOnChange) return;
        if (DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(2)) return;
        Load();
    }

    private void Load()
    {
        _loadedAt = DateTimeOffset.UtcNow;

        var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                {
                    var culture = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var json = File.ReadAllText(file);
                    var flat = new Dictionary<string, string>(StringComparer.Ordinal);

                    using var document = JsonDocument.Parse(json);
                    Flatten(document.RootElement, prefix: string.Empty, flat);

                    loaded[culture] = flat;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translations in {Directory} could not be read.", _directory);
            return;
        }

        if (!loaded.ContainsKey(NeutralCulture))
            _logger.LogError("No {Culture}.json in {Directory}. Every key will render as its own name.",
                NeutralCulture, _directory);

        _catalogues = loaded;
    }

    /// <summary>
    /// Nested JSON to dotted keys: <c>{"nav":{"runs":"Runs"}}</c> becomes <c>nav.runs</c>.
    ///
    /// Nesting is for the humans editing the file; the lookup wants one flat map.
    /// </summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, key, into);
                }
                break;

            case JsonValueKind.String:
                into[prefix] = element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                into[prefix] = element.ToString();
                break;
        }
    }
}

public sealed record MissingKey(string Culture, string Key);

/// <summary>
/// The framework-facing adapter. Views, data annotations and controllers all go through
/// <see cref="IStringLocalizer"/>, so this is what makes the JSON catalogue reachable from
/// <c>@Localizer["nav.runs"]</c> without any of them knowing where the text came from.
/// </summary>
public sealed class JsonStringLocalizer(JsonTranslations translations) : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            var found = translations.TryGet(CultureInfo.CurrentUICulture.Name, name, out var value);
            return new LocalizedString(name, value, resourceNotFound: !found);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = translations.TryGet(CultureInfo.CurrentUICulture.Name, name, out var value);
            var formatted = arguments.Length == 0
                ? value
                : string.Format(CultureInfo.CurrentCulture, value, arguments);
            return new LocalizedString(name, formatted, resourceNotFound: !found);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        translations.Catalogue(CultureInfo.CurrentUICulture.Name)
            .Select(pair => new LocalizedString(pair.Key, pair.Value, resourceNotFound: false));
}

/// <summary>
/// Every request for a localizer returns the same shared catalogue.
///
/// The framework default gives each view and each model its own resource file, which is how a
/// validation message ends up in English on a Persian form: the per-model file was never created,
/// so the lookup misses and the key falls through. One catalogue cannot miss for that reason.
/// </summary>
public sealed class JsonStringLocalizerFactory(JsonTranslations translations) : IStringLocalizerFactory
{
    public IStringLocalizer Create(Type resourceSource) => new JsonStringLocalizer(translations);

    public IStringLocalizer Create(string baseName, string location) => new JsonStringLocalizer(translations);
}
