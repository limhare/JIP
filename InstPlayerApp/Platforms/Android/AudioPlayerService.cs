using Android.Content;
using Android.Media;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using InstPlayerApp.Services;
using Object = Java.Lang.Object;

namespace InstPlayerApp.Platforms.Android;

public class AudioPlayerService : IAudioPlayerService
{
    private const string BuilderClass = "com/instplayer/app/SoundTouchExoPlayerBuilder";

    private IExoPlayer? _player;
    private PlayerEventListener? _listener;
    private SoundTouchBridge? _soundTouch;
    private readonly Context _context;
    private int _pendingPitch;
    private float _pendingTempo;

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
    public event Action<PlaybackStatus>? StatusChanged;
    public event Action<string?>? CurrentItemChanged;

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
            _soundTouch.SetPitch(_pendingPitch);
            _soundTouch.SetTempo(_pendingTempo);
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
        StartMediaSessionService();
    }

    // 미디어 세션 서비스: 잠금화면/블루투스·차량 버튼/Android Auto + 재생 중 포그라운드(화면꺼짐) 보호를
    // Media3가 관리한다. 앱이 화면에 있을 때 시작해야 하므로 플레이어 생성 시점에 띄운다.
    private void StartMediaSessionService()
    {
        try
        {
            var intent = new Intent();
            intent.SetClassName(_context, "com.instplayer.app.JipMediaLibraryService");
            _context.StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("JIP", $"media session service start failed: {ex.Message}");
        }
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

        var mediaItem = new MediaItem.Builder()
            .SetMediaId(filePath)!
            .SetUri(uri)!
            .SetMediaMetadata(new AndroidX.Media3.Common.MediaMetadata.Builder()
                .SetTitle(Path.GetFileNameWithoutExtension(filePath))!
                .Build())!
            .Build()!;
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
        NotifyUserPause();
        _player.Pause();
        Status = PlaybackStatus.Paused;
    }

    public void Stop()
    {
        if (_player == null) return;
        NotifyUserPause();
        _player.Stop();
        _player.SeekTo(0);
        Status = PlaybackStatus.Stopped;
    }

    // 사용자가 직접 멈춘 경우 통화 종료 후 자동 재개하지 않도록 Java 쪽에 알림
    private static void NotifyUserPause()
    {
        try
        {
            var cls = global::Android.Runtime.JNIEnv.FindClass(BuilderClass);
            var mid = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls, "onUserPause", "()V");
            global::Android.Runtime.JNIEnv.CallStaticVoidMethod(cls, mid);
        }
        catch
        {
        }
    }

    /// <summary>엔진에 실제 지정된 사용자 키 (Android Auto 키 버튼으로 바뀐 값 동기화용)</summary>
    public int GetEnginePitch()
    {
        try
        {
            var cls = global::Android.Runtime.JNIEnv.FindClass(BuilderClass);
            var mid = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls, "getUserPitch", "()F");
            return (int)Math.Round(global::Android.Runtime.JNIEnv.CallStaticFloatMethod(cls, mid));
        }
        catch
        {
            return _pendingPitch;
        }
    }

    public void SeekTo(TimeSpan position)
    {
        _player?.SeekTo((long)position.TotalMilliseconds);
    }

    public void SetPitchTempo(int semitones, float tempoPercent)
    {
        _pendingPitch = semitones;
        _pendingTempo = tempoPercent;
        if (_player == null) return;
        if (_soundTouch != null)
        {
            // 키: SoundTouch, 속도: Sonic(PlaybackParameters) — Java 쪽에서 분배
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

    // 작업마다 별도의 Java AudioExporter 인스턴스를 쓴다 — 하나를 공유하면 동시 작업의
    // 진행률/완료/결과 필드가 섞인다 (내보내기 중 녹음 저장 등).
    public async Task<string> ExportMp3Async(string inputPath, int pitchSemitones, float tempoPercent,
                                              string outputPath, IProgress<int>? progress = null,
                                              CancellationToken ct = default)
    {
        using var exporter = new AudioExportBridge();
        return await exporter.ExportAsync(inputPath, pitchSemitones, tempoPercent,
                                          outputPath, progress, ct);
    }

    public async Task<string> ExportMp3HQAsync(string inputPath, int pitchSemitones, float tempoPercent,
                                                string outputPath, IProgress<int>? progress = null,
                                                CancellationToken ct = default)
    {
        using var exporter = new AudioExportBridge();
        return await exporter.ExportHQAsync(inputPath, pitchSemitones, tempoPercent,
                                            outputPath, progress, ct);
    }

    public async Task<string> DecodeToWavAsync(string inputPath, string outputWavPath,
                                                IProgress<int>? progress = null,
                                                CancellationToken ct = default)
    {
        using var exporter = new AudioExportBridge();
        return await exporter.DecodeToWavAsync(inputPath, outputWavPath, progress, ct);
    }

    // 재생 중 출력 PCM(키/속도 적용본)을 WAV로 탭 — 오디오 체인 끝의 PcmTapProcessor 제어
    public bool StartOutputCapture(string wavPath)
    {
        try
        {
            var cls = global::Android.Runtime.JNIEnv.FindClass("com/instplayer/app/PcmTapProcessor");
            var mid = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls, "startTap", "(Ljava/lang/String;)Z");
            using var jPath = new Java.Lang.String(wavPath);
            return global::Android.Runtime.JNIEnv.CallStaticBooleanMethod(cls, mid,
                new global::Android.Runtime.JValue(jPath));
        }
        catch
        {
            return false;
        }
    }

    public string StopOutputCapture()
    {
        try
        {
            var cls = global::Android.Runtime.JNIEnv.FindClass("com/instplayer/app/PcmTapProcessor");
            var mid = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls, "stopTap", "()Ljava/lang/String;");
            nint h = global::Android.Runtime.JNIEnv.CallStaticObjectMethod(cls, mid);
            return h != nint.Zero
                ? global::Android.Runtime.JNIEnv.GetString(h, global::Android.Runtime.JniHandleOwnership.TransferLocalRef) ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    internal void OnStateEnded()
    {
        Status = PlaybackStatus.Stopped;
        StatusChanged?.Invoke(Status);
        // Android Auto가 넣은 다중 곡 대기열은 ExoPlayer가 직접 넘긴다. 앱 자동 다음곡은
        // 앱이 넣은 단일 곡이 끝났을 때만 (대기열 끝에서 앱 목록 곡이 끼어들지 않게).
        if (_player != null && _player.MediaItemCount > 1) return;
        PlaybackEnded?.Invoke();
    }

    // 전화/이어폰 분리/차량·블루투스 버튼/Auto 등 앱 밖에서 바뀐 재생 상태를 반영
    internal void OnIsPlayingChanged(bool isPlaying)
    {
        if (_player == null) return;
        if (isPlaying)
        {
            Status = PlaybackStatus.Playing;
        }
        else if (!_player.PlayWhenReady && Status == PlaybackStatus.Playing)
        {
            Status = PlaybackStatus.Paused;
        }
        else
        {
            return;
        }
        StatusChanged?.Invoke(Status);
    }

    internal void OnMediaItemTransition(MediaItem? item)
    {
        // 미디어 ID: 앱이 넣은 곡은 파일 경로, Auto 서비스가 넣은 곡은 "song:<부모>\n<경로>"
        string? id = item?.MediaId;
        string? path = id;
        if (id != null && id.StartsWith("song:"))
        {
            int nl = id.IndexOf('\n');
            path = nl >= 0 ? id[(nl + 1)..] : id[5..];
        }
        CurrentItemChanged?.Invoke(path);
    }

    public void Dispose()
    {
        if (_listener != null)
        {
            _player?.RemoveListener(_listener);
            _listener.Dispose();
            _listener = null;
        }
        // 플레이어는 Auto 서비스와 공유하는 프로세스 전역 객체 — 여기서 Release하지 않는다
        _player = null;
        _soundTouch?.Dispose();
        _soundTouch = null;
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

        public void OnIsPlayingChanged(bool isPlaying) => _service.OnIsPlayingChanged(isPlaying);

        public void OnMediaItemTransition(MediaItem? mediaItem, int reason) =>
            _service.OnMediaItemTransition(mediaItem);
    }
}
