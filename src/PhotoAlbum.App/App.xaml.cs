using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModernWpf;
using PhotoAlbum.Api;
using PhotoAlbum.App.Infrastructure;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Interfaces;
using PhotoAlbum.Data;
using PhotoAlbum.Data.Repositories;
using Serilog;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PhotoAlbum.App;

public partial class App : Application
{
    private IHost? _host;
    public IServiceProvider? Services => _host?.Services;

    // Resolved once at entry so crash handlers always have a valid path,
    // even if Serilog hasn't been initialized yet.
    private static readonly string _logDir =
        Path.Combine(AppContext.BaseDirectory, "log");

    private static string _markerPath = Path.Combine(_logDir, "starting.tmp");

    protected override async void OnStartup(StartupEventArgs e)
    {
        // ── Pre-init safety net ───────────────────────────────────────────────
        // Register AppDomain handler FIRST with a raw file writer so any crash
        // before Serilog is up still produces a file in the log folder.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;

        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch (Exception dirEx)
        {
            // Last-resort: try writing next to the EXE itself
            WriteFailsafeFile($"Could not create log directory: {dirEx}");
        }

        // Startup marker: written immediately, deleted on clean exit.
        // If it exists at next launch, the previous run crashed before OnExit.
        WriteMarker();

        // ── Serilog ───────────────────────────────────────────────────────────
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(_logDir, "photoalbum-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();
        }
        catch (Exception logEx)
        {
            WriteFailsafeFile($"Serilog init failed: {logEx}");
            throw;
        }

        // ── RunLogger ─────────────────────────────────────────────────────────
        RunLogger.Initialize(_logDir);
        RunLogger.Info("App", "Serilog initialized",
            Path.Combine(_logDir, "photoalbum-.log"));

        // Check for previous crash marker
        if (File.Exists(_markerPath))
            RunLogger.Warn("App",
                "Previous run crashed before clean exit (starting.tmp was present)");

        WriteMarker(); // rewrite after log is ready so timestamp is fresh

        // ── Post-init exception handlers ──────────────────────────────────────
        // These write to Serilog+RunLogger; AppDomain handler above is upgraded below.
        DispatcherUnhandledException += (_, ex) =>
        {
            var ctx = $"DispatcherUnhandledException (thread={System.Threading.Thread.CurrentThread.ManagedThreadId})";
            Log.Fatal(ex.Exception, "DispatcherUnhandledException: {Message}", ex.Exception.Message);
            RunLogger.CrashDump(ex.Exception, ctx);
            RunLogger.Close(exitCode: -1);
            Log.CloseAndFlush();
            // Do not set Handled=true — let WPF show the crash dialog
        };
        // Re-register AppDomain handler now that Serilog is ready
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            var inner = ex.ExceptionObject as Exception
                        ?? new Exception($"Non-Exception object: {ex.ExceptionObject}");
            var ctx = $"AppDomain.UnhandledException (isTerminating={ex.IsTerminating})";
            Log.Fatal(inner, "AppDomain.UnhandledException: {Message}", inner.Message);
            RunLogger.CrashDump(inner, ctx);
            RunLogger.Close(exitCode: -2);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "UnobservedTaskException: {Message}", ex.Exception.Message);
            RunLogger.Error("TaskScheduler", "UnobservedTaskException (fire-and-forget)", ex.Exception);
            ex.SetObserved();
        };

        // ── DI host ───────────────────────────────────────────────────────────
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoAlbum");
        Directory.CreateDirectory(appData);

        RunLogger.Info("App", "Building DI host");
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                var dbPath = Path.Combine(appData, "library.db");

                // Data
                services.AddSingleton(new DatabaseContext(dbPath));
                services.AddSingleton<IMediaItemRepository, MediaItemRepository>();
                services.AddSingleton<IAlbumRepository, AlbumRepository>();
                services.AddSingleton<ITagRepository, TagRepository>();
                services.AddSingleton<IPersonRepository, PersonRepository>();
                services.AddSingleton<IPlaceRepository, PlaceRepository>();
                services.AddSingleton<IEventRepository, EventRepository>();
                services.AddSingleton<IOperationLogRepository, OperationLogRepository>();
                services.AddSingleton<IUserSettingsRepository, UserSettingsRepository>();
                services.AddSingleton<IIndexedFolderRepository, IndexedFolderRepository>();

                // Rust interop
                services.AddSingleton<IRustHasher, RustHasher>();
                services.AddSingleton<IRustScanner, RustScanner>();
                services.AddSingleton<IRustThumbnailer, RustThumbnailer>();
                services.AddSingleton<IRustCopyEngine, RustCopyEngine>();
                services.AddSingleton<IRustPHasher, RustPHasher>();

                // App services
                services.AddSingleton<HiddenContentService>();
                services.AddSingleton<PhotoAlbum.Core.Interfaces.IHiddenContentManager>(
                    sp => sp.GetRequiredService<HiddenContentService>());
                services.AddSingleton<StartupIntegrityService>();
                services.AddSingleton<UserSettingsService>();
                services.AddSingleton<ThumbnailManager>();
                services.AddSingleton<IndexOrchestrator>();
                services.AddSingleton<TagService>();
                services.AddSingleton<AlbumService>();
                services.AddSingleton<ClonePlannerService>();
                services.AddSingleton<ExportService>();
                services.AddSingleton<UndoService>();
                services.AddSingleton<BinaryManifestService>();
                services.AddSingleton<PHashService>();
                services.AddSingleton<DuplicateFinderService>();

                // ViewModels
                services.AddTransient<ViewModels.LibraryViewModel>();
                services.AddTransient<ViewModels.DetailViewModel>();

                // Windows
                services.AddTransient<MainWindow>();
            })
            .Build();

        // Apply saved theme before the window is created
        var settingsSvc = _host.Services.GetRequiredService<UserSettingsService>();
        var savedTheme = await settingsSvc.GetThemeAsync();
        ApplySavedTheme(savedTheme);

        ThemeManager.Current.ActualApplicationThemeChanged += (_, _) =>
            SyncThemeDictionary(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);

        RunLogger.Info("App", "Host starting");
        await _host.StartAsync();

        RunLogger.Info("App", "StartupIntegrityService running");
        var integrity = _host.Services.GetRequiredService<StartupIntegrityService>();
        await integrity.RunAsync();
        RunLogger.Info("App", "StartupIntegrityService complete");

        RunLogger.Info("App", "HiddenContentService initializing");
        var hiddenSvc = _host.Services.GetRequiredService<HiddenContentService>();
        await hiddenSvc.InitializeAsync();
        RunLogger.Info("App", "HiddenContentService ready");

        _ = Task.Run(async () =>
        {
            try
            {
                RunLogger.Info("App", "BinaryManifestService starting (background)");
                var manifest = _host.Services.GetRequiredService<BinaryManifestService>();
                await manifest.RunAsync();
                RunLogger.Info("App", "BinaryManifestService complete");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Binary manifest check failed (non-fatal)");
                RunLogger.Warn("App", "BinaryManifestService failed (non-fatal)", ex);
            }
        });

        RunLogger.Info("App", "Creating MainWindow");
        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
        RunLogger.Info("App", "MainWindow shown — UI ready");

        RunLogger.Info("App", "Starting local REST API on 127.0.0.1:5150 (background)");
        _ = StartApiAsync(_host.Services, hiddenSvc);

        base.OnStartup(e);
    }

    // ── Theme helpers ─────────────────────────────────────────────────────────

    internal static void ApplySavedTheme(string tag)
    {
        ThemeManager.Current.ApplicationTheme = tag switch
        {
            "Dark"  => ApplicationTheme.Dark,
            "Light" => ApplicationTheme.Light,
            _       => null,
        };
        SyncThemeDictionary(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
    }

    private static void SyncThemeDictionary(bool isLight)
    {
        var dicts = Current.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/") == true);
        if (old is not null) dicts.Remove(old);

        var uri = new Uri(isLight
            ? "pack://application:,,,/Resources/Themes/Light.xaml"
            : "pack://application:,,,/Resources/Themes/Dark.xaml",
            UriKind.Absolute);
        dicts.Add(new ResourceDictionary { Source = uri });
    }

    private static async Task StartApiAsync(IServiceProvider sp, HiddenContentService hidden)
    {
        try
        {
            await LocalApiHost.StartAsync(
                sp.GetRequiredService<IMediaItemRepository>(),
                sp.GetRequiredService<IAlbumRepository>(),
                sp.GetRequiredService<ITagRepository>(),
                sp.GetRequiredService<IPersonRepository>(),
                sp.GetRequiredService<IPlaceRepository>(),
                sp.GetRequiredService<IEventRepository>(),
                hidden);
            RunLogger.Info("App", "Local REST API started on http://127.0.0.1:5150");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Local API failed to start (non-fatal)");
            RunLogger.Warn("App", "Local REST API failed to start (non-fatal)", ex);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        RunLogger.Info("App", $"OnExit called — code {e.ApplicationExitCode}");
        DeleteMarker();
        await LocalApiHost.StopAsync();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        RunLogger.Close(e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    // ── Pre-init crash handling ───────────────────────────────────────────────

    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs ex)
    {
        var msg = ex.ExceptionObject is Exception e
            ? $"{e.GetType().FullName}: {e.Message}\n{e.StackTrace}"
            : ex.ExceptionObject?.ToString() ?? "(unknown)";
        WriteFailsafeFile($"[PRE-INIT CRASH]\n{msg}");
    }

    private static void WriteFailsafeFile(string content)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            var path = Path.Combine(_logDir, $"crash-preinit-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"{DateTime.Now:O}\n{content}");
        }
        catch { /* nothing left to do */ }
    }

    private static void WriteMarker()
    {
        try { File.WriteAllText(_markerPath, DateTime.Now.ToString("O")); }
        catch { }
    }

    private static void DeleteMarker()
    {
        try { File.Delete(_markerPath); }
        catch { }
    }
}
