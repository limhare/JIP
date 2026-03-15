using Android.Content;
using Android.Media;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using InstPlayerApp.Services;
using Object = Java.Lang.Object;

namespace InstPlayerApp.Platforms.Android;

public class AudioPlayerService : IAudioPlayerService
{
    private IExoPlayer? _player;
    private PlayerEventListener? _listener;
    private SoundTouchBridge? _soundTouch;
    private AudioExportBridge? _exporter;
    private readonly Context _context;

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;
    public TimeSpan Duration => _player != null && _player.Duration > 0
        ? TimeSpan.FromMilliseconds(_player.Duration)
        : TimeSpan.Zero;
    public TimeSpan Position => _player != null
        ? TimeSpan.FromMilliseconds(_player.CurrentPosition)
        : TimeSpan.Zero;

    // Volume 0.0~1.0 → 시스템 볼륨(STREAM_MUSIC)을 직접 제어
    public float Volume
    {
        get
        {
            var am = GetAudioManager();
            if (am == null) return 1.0f;
            var max = am.GetStreamMaxVolume(global::Android.Media.Stream.Music);
            var cur = am.GetStreamVolume(global::Android.Media.Stream.Music);
            return max > 0 ? (float)cur / max : 1.0f;
        }
        set
        {
            var am = GetAudioManager();
            if (am == null) return;
            var max = am.GetStreamMaxVolume(global::Android.Media.Stream.Music);
            var target = (int)Math.Round(Math.Clamp(value, 0f, 1f) * max);
            am.SetStreamVolume(global::Android.Media.Stream.Music, target, 0);
            if (_player != null) _player.Volume = 1.0f;
        }
    }

    private static AudioManager? GetAudioManager() =>
        global::Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;

    public float CurrentLevel { get; private set; }

    public event Action? PlaybackEnded;
    public event Action<float>? LevelUpdated;

    public AudioPlayerService()
    {
        _context = global::Android.App.Application.Context;
    }

    private void EnsurePlayer()
    {
        if (_player != null) return;
        try
        {
            _soundTouch = new SoundTouchBridge();
            _player = _soundTouch.CreatePlayer(_context);
        }
        catch (Exception ex)
        {
            // SoundTouch load failed — fall back to standard ExoPlayer
            global::Android.Util.Log.Warn("JIP", $"SoundTouch init failed: {ex.Message}. Using default player.");
            _soundTouch?.Dispose();
            _soundTouch = null;
            _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(_context).Build()!;
        }
        _listener = new PlayerEventListener(this);
        _player.AddListener(_listener);
        _player.Volume = 1.0f;
    }

    public void LoadAndPlay(string filePath)
    {
        EnsurePlayer();
        _player!.Stop();
        _player.ClearMediaItems();

        global::Android.Net.Uri? uri;
        if (filePath.StartsWith("content://") || filePath.StartsWith("file://") || filePath.StartsWith("http"))
            uri = global::Android.Net.Uri.Parse(filePath);
        else
            uri = global::Android.Net.Uri.FromFile(new Java.IO.File(filePath));

        var mediaItem = MediaItem.FromUri(uri!);
        _player.SetMediaItem(mediaItem);
        _player.Prepare();
        _player.Play();
        Status = PlaybackStatus.Playing;
    }

    public void Play()
    {
        if (_player == null) return;
        _player.Play();
        Status = PlaybackStatus.Playing;
    }

    public void Pause()
    {
        if (_player == null) return;
        _player.Pause();
        Status = PlaybackStatus.Paused;
    }

    public void Stop()
    {
        if (_player == null) return;
        _player.Stop();
        _player.SeekTo(0);
        Status = PlaybackStatus.Stopped;
    }

    public void SeekTo(TimeSpan position)
    {
        _player?.SeekTo((long)position.TotalMilliseconds);
    }

    public void SetPitchTempo(int semitones, float tempoPercent)
    {
        if (_player == null) return;
        if (_soundTouch != null)
        {
            // SoundTouch: semitones directly, tempo as % offset (0 = normal)
            _soundTouch.SetPitch(semitones);
            _soundTouch.SetTempo(tempoPercent);
        }
        else
        {
            // Fallback: ExoPlayer's built-in Sonic processor
            float pitch = (float)Math.Pow(2, semitones / 12.0);
            float speed = 1.0f + tempoPercent / 100.0f;
            speed = Math.Clamp(speed, 0.25f, 4.0f);
            pitch = Math.Clamp(pitch, 0.25f, 4.0f);
            _player.PlaybackParameters = new PlaybackParameters(speed, pitch);
        }
    }

    public async Task<string> ExportMp3Async(string inputPath, int pitchSemitones, float tempoPercent,
                                              string outputPath, IProgress<int>? progress = null,
                                              CancellationToken ct = default)
    {
        _exporter ??= new AudioExportBridge();
        return await _exporter.ExportAsync(inputPath, pitchSemitones, tempoPercent,
                                           outputPath, progress, ct);
    }

    internal void OnStateEnded()
    {
        Status = PlaybackStatus.Stopped;
        PlaybackEnded?.Invoke();
    }

    public void Dispose()
    {
        if (_listener != null)
        {
            _player?.RemoveListener(_listener);
            _listener.Dispose();
            _listener = null;
        }
        _player?.Release();
        _player = null;
        _soundTouch?.Dispose();
        _soundTouch = null;
        _exporter?.Dispose();
        _exporter = null;
    }

    // Separate Java.Lang.Object subclass for the listener
    private class PlayerEventListener : Object, IPlayerListener
    {
        private readonly AudioPlayerService _service;
        public PlayerEventListener(AudioPlayerService service) => _service = service;

        public void OnPlaybackStateChanged(int playbackState)
        {
            // ExoPlayer STATE_ENDED = 4
            if (playbackState == 4)
                _service.OnStateEnded();
        }
    }
}
