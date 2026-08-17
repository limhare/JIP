namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// 16-bit PCM WAV 두 개(반주 + 마이크)를 오프라인 믹스.
/// 반주는 mrOffsetMs 지점부터 사용해 마이크 시작 시점과 싱크를 맞춘다.
/// </summary>
internal static class WavMixer
{
    private sealed class WavData
    {
        public int SampleRate;
        public int Channels;
        public short[] Samples = Array.Empty<short>();
        public long Frames => Samples.LongLength / Math.Max(1, Channels);
    }

    public static void Mix(string mrWavPath, string micWavPath, string outputWavPath, double mrOffsetMs)
    {
        WavData mr = Load(mrWavPath);
        WavData mic = Load(micWavPath);

        // 마이크를 반주 샘플레이트로 맞춤 (선형 보간)
        if (mic.SampleRate != mr.SampleRate)
        {
            mic.Samples = ResampleMono(ToMono(mic), mic.SampleRate, mr.SampleRate);
            mic.Channels = 1;
            mic.SampleRate = mr.SampleRate;
        }
        short[] micMono = (mic.Channels == 1) ? mic.Samples : ToMono(mic);

        int outCh = mr.Channels;
        long mrSkip = (long)(mrOffsetMs / 1000.0 * mr.SampleRate);
        long mrFrames = Math.Max(0, mr.Frames - mrSkip);
        long micFrames = micMono.LongLength;
        // 결과물 길이 = 마이크 녹음 길이 (반주는 그 구간만큼만 사용)
        long frames = micFrames;

        using var fs = File.Create(outputWavPath);
        using var bw = new BinaryWriter(fs);
        long dataLen = frames * outCh * 2;
        WriteHeader(bw, mr.SampleRate, (short)outCh, dataLen);

        for (long f = 0; f < frames; f++)
        {
            int micVal = (f < micFrames) ? micMono[f] : 0;
            for (int c = 0; c < outCh; c++)
            {
                long mrIdx = (mrSkip + f) * mr.Channels + Math.Min(c, mr.Channels - 1);
                int mrVal = (f < mrFrames && mrIdx < mr.Samples.LongLength) ? mr.Samples[mrIdx] : 0;
                int sum = mrVal + micVal;
                if (sum > 32767) sum = 32767;
                else if (sum < -32768) sum = -32768;
                bw.Write((short)sum);
            }
        }
    }

    private static short[] ToMono(WavData w)
    {
        if (w.Channels == 1) return w.Samples;
        long frames = w.Frames;
        var mono = new short[frames];
        for (long f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < w.Channels; c++) sum += w.Samples[f * w.Channels + c];
            mono[f] = (short)(sum / w.Channels);
        }
        return mono;
    }

    private static short[] ResampleMono(short[] src, int srcRate, int dstRate)
    {
        long dstLen = src.LongLength * dstRate / srcRate;
        var dst = new short[dstLen];
        for (long i = 0; i < dstLen; i++)
        {
            double srcPos = (double)i * srcRate / dstRate;
            long i0 = (long)srcPos;
            long i1 = Math.Min(i0 + 1, src.LongLength - 1);
            double t = srcPos - i0;
            dst[i] = (short)(src[i0] * (1.0 - t) + src[i1] * t);
        }
        return dst;
    }

    private static WavData Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") throw new Exception("WAV 형식이 아닙니다: " + path);
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new Exception("WAV 형식이 아닙니다: " + path);

        int sampleRate = 0, channels = 0, bits = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            string chunkId = new string(br.ReadChars(4));
            int chunkSize = br.ReadInt32();
            if (chunkId == "fmt ")
            {
                long chunkStart = fs.Position;
                br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                fs.Position = chunkStart + chunkSize;
            }
            else if (chunkId == "data")
            {
                if (bits != 16) throw new Exception($"16-bit PCM만 지원합니다 ({bits}-bit): " + path);
                long count = Math.Min(chunkSize, fs.Length - fs.Position) / 2;
                var samples = new short[count];
                var bytes = br.ReadBytes((int)(count * 2));
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                return new WavData { SampleRate = sampleRate, Channels = channels, Samples = samples };
            }
            else
            {
                fs.Position += chunkSize;
            }
        }
        throw new Exception("data 청크를 찾을 수 없습니다: " + path);
    }

    private static void WriteHeader(BinaryWriter bw, int sampleRate, short channels, long dataLen)
    {
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
}
