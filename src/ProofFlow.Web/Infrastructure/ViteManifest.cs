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
    private readonly ILogger<ViteManifest> _logger;
    private readonly string _path;
    private readonly bool _reloadOnChange;
    private readonly Lock _gate = new();

    private Dictionary<string, ManifestEntry> _entries;
    private DateTime _loadedStamp;

    public ViteManifest(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ViteManifest> logger)
    {
        _logger = logger;

        UseDevServer = environment.IsDevelopment()
            && configuration.GetValue("Vite:UseDevServer", false);
        DevServerUrl = configuration["Vite:DevServerUrl"] ?? "http://localhost:5173";

        _path = Path.Combine(environment.WebRootPath ?? "wwwroot", "build", "manifest.json");

        // Vite writes new hashed filenames on every build. Read once, the running application keeps
        // pointing at files that no longer exist, and every page renders unstyled with no error —
        // which is not merely inconvenient: an unstyled page passes an automated contrast audit
        // trivially, so it turns the accessibility gate green for the worst possible reason.
        _reloadOnChange = environment.IsDevelopment();

        _entries = Load();

        if (_entries.Count == 0 && !UseDevServer)
        {
            logger.LogWarning(
                "No Vite manifest at {Path}. The interface will render unstyled until `npm run build` " +
                "has been executed in src/ProofFlow.Web.", _path);
        }
    }

    /// <summary>True while the page should load assets from Vite's dev server (hot reload).</summary>
    public bool UseDevServer { get; }

    public string DevServerUrl { get; }

    public bool IsBuilt => _entries.Count > 0;

    /// <summary>The script and stylesheet URLs for one entry point, already prefixed with /build/.</summary>
    public (string? Script, IReadOnlyList<string> Styles) Resolve(string entry)
    {
        ReloadIfChanged();

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

    /// <summary>
    /// Picks up a rebuild between requests, in development only.
    ///
    /// Compares the file's write time rather than watching it: a watcher would fire while Vite is
    /// still writing and read a half-flushed file, and the cost of a stat per page render is
    /// nothing next to a developer wondering why their CSS change did nothing.
    /// </summary>
    private void ReloadIfChanged()
    {
        if (!_reloadOnChange) return;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(_path);
        }
        catch (IOException)
        {
            return;
        }

        if (stamp == _loadedStamp) return;

        lock (_gate)
        {
            if (stamp == _loadedStamp) return;
            _entries = Load();
            _logger.LogInformation("Reloaded the Vite manifest after a rebuild.");
        }
    }

    private Dictionary<string, ManifestEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];

            // Stamped before the read, not after: if Vite writes again while this read is in
            // flight, the older stamp means the next request reloads rather than trusting a
            // partially-written file it already cached.
            _loadedStamp = File.GetLastWriteTimeUtc(_path);

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(json, Options) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Vite manifest at {Path} could not be read.", _path);
            return [];
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ManifestEntry(string File, string[]? Css, string[]? Imports);
}
