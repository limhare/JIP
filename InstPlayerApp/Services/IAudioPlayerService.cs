namespace InstPlayerApp.Services;

public enum PlaybackStatus { Stopped, Playing, Paused }

public interface IAudioPlayerService
{
    PlaybackStatus Status { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    float Volume { get; set; }
    float CurrentLevel { get; }

    void LoadAndPlay(string filePath);
    void Play();
    void Pause();
    void Stop();
    void SeekTo(TimeSpan position);
    void SetPitchTempo(int semitones, float tempoPercent);
    Task<string> ExportMp3Async(string inputPath, int pitchSemitones, float tempoPercent,
                                string outputPath, IProgress<int>? progress = null,
                                CancellationToken ct = default);
    Task<string> ExportMp3HQAsync(string inputPath, int pitchSemitones, float tempoPercent,
                                  string outputPath, IProgress<int>? progress = null,
                                  CancellationToken ct = default);
    Task<string> DecodeToWavAsync(string inputPath, string outputWavPath,
                                  IProgress<int>? progress = null,
                                  CancellationToken ct = default);
    bool StartOutputCapture(string wavPath);
    string StopOutputCapture();
    int GetEnginePitch();
    void Dispose();

    event Action? PlaybackEnded;
    event Action<float>? LevelUpdated;
    /// <summary>앱 밖(통화, 이어폰 분리, 차량/블루투스 버튼, Auto)에서 바뀐 재생 상태</summary>
    event Action<PlaybackStatus>? StatusChanged;
    /// <summary>재생 중인 곡이 바뀜 (Auto 대기열 진행 등). 인자: 파일 경로</summary>
    event Action<string?>? CurrentItemChanged;
}
