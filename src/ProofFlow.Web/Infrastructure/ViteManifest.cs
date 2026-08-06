using System.Text.Json;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Turns an entry name Razor knows ("Scripts/main.ts") into the hashed files Vite actually wrote.
///
/// Read once and cached: the manifest cannot change while the process runs in production, and the
/// alternative was a file read per page render. In development the Vite dev server is used
/// instead, so nothing here is consulted at all.
/// </summary>
public sealed class ViteManifest
{
    private readonly Dictionary<string, ManifestEntry> _entries;
    private readonly ILogger<ViteManifest> _logger;

    public ViteManifest(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ViteManifest> logger)
    {
        _logger = logger;

        UseDevServer = environment.IsDevelopment()
            && configuration.GetValue("Vite:UseDevServer", false);
        DevServerUrl = configuration["Vite:DevServerUrl"] ?? "http://localhost:5173";

        var path = Path.Combine(environment.WebRootPath ?? "wwwroot", "build", "manifest.json");
        _entries = Load(path, logger);

        if (_entries.Count == 0 && !UseDevServer)
        {
            // Say it once, loudly, at startup. The alternative is a page that renders with no
            // styling and no explanation, which people debug for an hour.
            logger.LogWarning(
                "No Vite manifest at {Path}. The interface will render unstyled until `npm run build` " +
                "has been executed in src/ProofFlow.Web.", path);
        }
    }

    /// <summary>True while the page should load assets from Vite's dev server (hot reload).</summary>
    public bool UseDevServer { get; }

    public string DevServerUrl { get; }

    public bool IsBuilt => _entries.Count > 0;

    /// <summary>The script and stylesheet URLs for one entry point, already prefixed with /build/.</summary>
    public (string? Script, IReadOnlyList<string> Styles) Resolve(string entry)
    {
        if (!_entries.TryGetValue(entry, out var manifestEntry))
            return (null, []);

        var styles = new List<string>();
        CollectCss(entry, styles, depth: 0);

        return ($"/build/{manifestEntry.File}", styles);
    }

    /// <summary>
    /// Follows the import graph for stylesheets.
    ///
    /// A CSS file imported by a chunk that the entry imports does not appear on the entry's own
    /// <c>css</c> array — only on the chunk's. Stopping at the entry is how a build ends up with
    /// half its styles missing, which looks like a CSS bug and is a manifest-reading bug.
    /// </summary>
    private void CollectCss(string key, List<string> into, int depth)
    {
        if (depth > 16 || !_entries.TryGetValue(key, out var entry)) return;

        foreach (var css in entry.Css ?? [])
        {
            var url = $"/build/{css}";
            if (!into.Contains(url)) into.Add(url);
        }

        foreach (var import in entry.Imports ?? [])
            CollectCss(import, into, depth + 1);
    }

    private static Dictionary<string, ManifestEntry> Load(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path)) return [];

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(json, Options) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The Vite manifest at {Path} could not be read.", path);
            return [];
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ManifestEntry(string File, string[]? Css, string[]? Imports);
}
