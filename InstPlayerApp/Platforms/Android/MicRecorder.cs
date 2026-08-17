using Android.Media;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// 마이크를 44.1kHz 16bit mono WAV로 녹음.
/// Start() → 백그라운드 스레드가 파일에 기록 → Stop()에서 WAV 헤더 확정.
/// </summary>
internal class MicRecorder : IDisposable
{
    private const int SampleRate = 44100;

    private AudioRecord? _record;
    private Thread? _thread;
    private volatile bool _running;
    private string _outputPath = "";
    private long _dataBytes;

    public bool IsRecording => _running;

    public void Start(string outputPath)
    {
        if (_running) return;

        int minBuf = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        int bufSize = Math.Max(minBuf * 2, 16384);
        _record = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufSize);
        if (_record.State != State.Initialized)
        {
            _record.Release();
            _record = null;
            throw new Exception("마이크를 초기화할 수 없습니다.");
        }

        _outputPath = outputPath;
        _dataBytes = 0;
        using (var fs = File.Create(outputPath))
        {
            WriteWavHeader(fs, SampleRate, 1, 0);
        }

        _running = true;
        _record.StartRecording();
        _thread = new Thread(ReadLoop) { IsBackground = true };
        _thread.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[16384];
        try
        {
            using var fs = new FileStream(_outputPath, FileMode.Append, FileAccess.Write);
            while (_running && _record != null)
            {
                int n = _record.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    fs.Write(buf, 0, n);
                    _dataBytes += n;
                }
            }
        }
        catch
        {
            // 스레드 내 예외는 무시 — Stop()에서 파일 상태로 판단
        }
    }

    /// <summary>녹음 중지, WAV 헤더 확정 후 파일 경로 반환.</summary>
    public string Stop()
    {
        _running = false;
        _thread?.Join(3000);
        _thread = null;
        try
        {
            _record?.Stop();
        }
        catch
        {
        }
        _record?.Release();
        _record = null;

        using (var fs = new FileStream(_outputPath, FileMode.Open, FileAccess.ReadWrite))
        {
            WriteWavHeader(fs, SampleRate, 1, _dataBytes);
        }
        return _outputPath;
    }

    private static void WriteWavHeader(System.IO.Stream s, int sampleRate, short channels, long dataLen)
    {
        s.Seek(0, SeekOrigin.Begin);
        using var bw = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        int byteRate = sampleRate * channels * 2;
        bw.Write("RIFF"u8.ToArray());
        bw.Write((int)(36 + dataLen));
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write((int)dataLen);
    }

    public void Dispose()
    {
        if (_running)
        {
            try
            {
                Stop();
            }
            catch
            {
            }
        }
    }
}
