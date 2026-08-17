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
    void Dispose();

    event Action? PlaybackEnded;
    event Action<float>? LevelUpdated;
}
