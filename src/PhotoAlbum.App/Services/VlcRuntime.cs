using LibVLCSharp.Shared;
using System.IO;

namespace PhotoAlbum.App.Services;

/// <summary>
/// One-time initialization of the bundled libvlc native runtime
/// (libvlc\win-x64 ships with the app — VLC decodes H.264/HEVC internally,
/// so video playback needs no Windows codec packs).
/// </summary>
public static class VlcRuntime
{
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>Idempotent; throws only if the bundled libvlc folder is missing.</summary>
    public static void Ensure()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            var libPath = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (Directory.Exists(libPath))
                LibVLCSharp.Shared.Core.Initialize(libPath);
            else
                LibVLCSharp.Shared.Core.Initialize(); // fall back to default probing
            _initialized = true;
            RunLogger.Info("Vlc", $"libvlc initialized from {libPath}");
        }
    }
}
