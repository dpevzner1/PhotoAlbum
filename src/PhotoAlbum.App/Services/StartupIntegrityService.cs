using Microsoft.Extensions.Logging;
using PhotoAlbum.App.Infrastructure;
using PhotoAlbum.Data;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Runs at startup: verifies Rust DLL is loadable, runs DB migrations,
/// and logs a diagnostic summary.
/// </summary>
public sealed class StartupIntegrityService
{
    private readonly DatabaseContext _db;
    private readonly ILogger<StartupIntegrityService> _log;

    public StartupIntegrityService(DatabaseContext db, ILogger<StartupIntegrityService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _log.LogInformation("StartupIntegrityService: begin");

        // 1. Verify Rust DLL is loaded
        try
        {
            var code = RustInterop.pa_health_check();
            if (code != RustInterop.ERR_OK)
                throw new InvalidOperationException($"pa_health_check returned {code}");
            _log.LogInformation("Rust core DLL: OK");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rust core DLL failed to load — check that photoalbum_core.dll is present");
            throw;
        }

        // 2. Run DB migrations
        await _db.EnsureInitializedAsync(ct);
        _log.LogInformation("Database: migrations applied");

        _log.LogInformation("StartupIntegrityService: complete");
    }
}
