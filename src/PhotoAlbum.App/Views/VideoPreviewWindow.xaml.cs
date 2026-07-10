using LibVLCSharp.Shared;
using PhotoAlbum.App.Services;
using System.IO;
using System.Windows;

namespace PhotoAlbum.App.Views;

/// <summary>
/// Standalone play/pause preview player (bundled libvlc). Used for phone
/// videos (played from a temp download) and reusable for any local file.
/// Optionally deletes the file when the window closes (temp previews).
/// </summary>
public partial class VideoPreviewWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly string _path;
    private readonly bool _deleteOnClose;
    private bool _dragging;

    public VideoPreviewWindow(string path, string title, bool deleteOnClose = false)
    {
        InitializeComponent();
        Title = title;
        _path = path;
        _deleteOnClose = deleteOnClose;

        VlcRuntime.Ensure();
        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        VlcView.MediaPlayer = _player;

        _player.TimeChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            var total = _player.Length;
            if (!_dragging && total > 0) Seek.Value = (double)e.Time / total * 1000;
            TimeText.Text = $"{Fmt(e.Time)} / {Fmt(Math.Max(total, 0))}";
        });
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            PlayPauseIcon.Symbol = ModernWpf.Controls.Symbol.Play);

        Loaded += (_, _) =>
        {
            using var media = new Media(_libVlc, new Uri(_path));
            _player.Play(media);
        };
        Closed += (_, _) =>
        {
            var p = _player; var v = _libVlc;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { p.Stop(); p.Dispose(); v.Dispose(); } catch { }
                if (_deleteOnClose) { try { File.Delete(_path); } catch { } }
            });
        };
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) { _player.Pause(); PlayPauseIcon.Symbol = ModernWpf.Controls.Symbol.Play; }
        else                   { _player.Play();  PlayPauseIcon.Symbol = ModernWpf.Controls.Symbol.Pause; }
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        _player.Mute = !_player.Mute;
        MuteIcon.Symbol = _player.Mute ? ModernWpf.Controls.Symbol.Mute : ModernWpf.Controls.Symbol.Volume;
    }

    private void Seek_DragStarted(object s, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _dragging = true;

    private void Seek_DragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _dragging = false;
        if (_player.Length > 0) _player.Time = (long)(Seek.Value / 1000 * _player.Length);
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
