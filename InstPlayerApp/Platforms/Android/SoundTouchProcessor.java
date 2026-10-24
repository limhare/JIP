package com.instplayer.app;

import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.C;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * ExoPlayer AudioProcessor that uses SoundTouch for high-quality pitch shifting.
 * 속도(tempo)는 Sonic(PlaybackParameters)이 담당 — 여기선 키만 처리.
 * 입력 형식(16-bit/float) 그대로 출력한다 (후단 무음스킵 프로세서가 16-bit만 받음).
 * 네이티브 핸들은 스레드 안전하지 않으므로 모든 접근을 this로 동기화한다.
 */
public class SoundTouchProcessor implements AudioProcessor {

    static {
        System.loadLibrary("soundtouch4c");
    }

    // SoundTouch C API via JNI (soundtouch4c.h)
    private static native long  st_createInstance();
    private static native void  st_destroyInstance(long handle);
    private static native void  st_setSampleRate(long handle, int sampleRate);
    private static native void  st_setChannels(long handle, int channels);
    private static native void  st_setPitchSemiTones(long handle, float semitones);
    private static native void  st_setTempoChange(long handle, float tempo); // % change, 0 = normal
    private static native void  st_putSamples(long handle, float[] samples, int numSamples);
    private static native int   st_receiveSamples(long handle, float[] outBuf, int maxSamples);
    private static native int   st_numSamples(long handle);
    private static native void  st_flush(long handle);
    private static native void  st_clear(long handle);

    private long    stHandle    = 0;
    private AudioFormat inputFormat  = AudioFormat.NOT_SET;
    private AudioFormat outputFormat = AudioFormat.NOT_SET;
    private AudioFormat pendingFormat = AudioFormat.NOT_SET; // configure() 후 flush()에서 적용

    private float pendingPitch = 0f;   // semitones

    private ByteBuffer outputBuffer = EMPTY_BUFFER;
    private boolean    inputEnded   = false;
    private float[]    inScratch    = new float[0];
    private float[]    outScratch   = new float[0];

    // --- Public control API ---

    public synchronized void setPitch(float semitones) {
        pendingPitch = semitones;
        if (stHandle != 0) st_setPitchSemiTones(stHandle, semitones);
    }

    /** 속도는 Sonic 담당 — 호환을 위해 남겨두되 SoundTouch tempo는 항상 0 유지. */
    public synchronized void setTempo(float tempoPercent) {
    }

    // --- AudioProcessor interface ---

    @Override
    public synchronized AudioFormat configure(AudioFormat inputAudioFormat) throws UnhandledAudioFormatException {
        int encoding = inputAudioFormat.encoding;
        if (encoding != C.ENCODING_PCM_FLOAT && encoding != C.ENCODING_PCM_16BIT) {
            throw new UnhandledAudioFormatException(inputAudioFormat);
        }
        // AudioProcessor 계약: 새 형식은 flush() 시점에 적용 (이전 트랙 잔여 오디오 보존)
        pendingFormat = inputAudioFormat;
        return inputAudioFormat;
    }

    @Override
    public synchronized boolean isActive() {
        return pendingFormat != AudioFormat.NOT_SET || inputFormat != AudioFormat.NOT_SET;
    }

    @Override
    public synchronized void queueInput(ByteBuffer buffer) {
        if (stHandle == 0 || !buffer.hasRemaining()) return;

        int bytesAvail = buffer.remaining();
        int count;
        if (inputFormat.encoding == C.ENCODING_PCM_16BIT) {
            count = bytesAvail / 2;
            ensureIn(count);
            java.nio.ShortBuffer sb = buffer.asShortBuffer();
            for (int i = 0; i < count; i++) inScratch[i] = sb.get(i) / 32768f;
        } else {
            count = bytesAvail / 4;
            ensureIn(count);
            buffer.asFloatBuffer().get(inScratch, 0, count);
        }
        buffer.position(buffer.position() + bytesAvail);

        int samplesPerChannel = count / inputFormat.channelCount;
        if (inScratch.length == count) {
            st_putSamples(stHandle, inScratch, samplesPerChannel);
        } else {
            float[] exact = new float[count];
            System.arraycopy(inScratch, 0, exact, 0, count);
            st_putSamples(stHandle, exact, samplesPerChannel);
        }

        drainToOutputBuffer();
    }

    @Override
    public synchronized void queueEndOfStream() {
        inputEnded = true;
        if (stHandle != 0) st_flush(stHandle);
        drainToOutputBuffer();
    }

    @Override
    public synchronized ByteBuffer getOutput() {
        ByteBuffer out = outputBuffer;
        outputBuffer = EMPTY_BUFFER;
        return out;
    }

    @Override
    public synchronized boolean isEnded() {
        return inputEnded && outputBuffer == EMPTY_BUFFER;
    }

    @Override
    public synchronized void flush() {
        inputEnded = false;
        outputBuffer = EMPTY_BUFFER;
        if (pendingFormat != AudioFormat.NOT_SET && !pendingFormat.equals(inputFormat)) {
            inputFormat = pendingFormat;
            outputFormat = pendingFormat;
            recreateSoundTouch();
        } else if (stHandle != 0) {
            st_clear(stHandle);
        }
    }

    @Override
    public synchronized void reset() {
        inputEnded = false;
        outputBuffer = EMPTY_BUFFER;
        if (stHandle != 0) {
            st_destroyInstance(stHandle);
            stHandle = 0;
        }
        inputFormat   = AudioFormat.NOT_SET;
        outputFormat  = AudioFormat.NOT_SET;
        pendingFormat = AudioFormat.NOT_SET;
    }

    // --- Private helpers (호출자가 this 잠금 보유) ---

    private void ensureIn(int n) {
        if (inScratch.length < n) inScratch = new float[n];
    }

    private void recreateSoundTouch() {
        if (stHandle != 0) st_destroyInstance(stHandle);
        stHandle = st_createInstance();
        st_setSampleRate(stHandle, inputFormat.sampleRate);
        st_setChannels(stHandle, inputFormat.channelCount);
        st_setPitchSemiTones(stHandle, pendingPitch);
        st_setTempoChange(stHandle, 0f);
    }

    private void drainToOutputBuffer() {
        if (stHandle == 0) return;

        int available = st_numSamples(stHandle);
        if (available <= 0) return;

        int floatCount = available * inputFormat.channelCount;
        if (outScratch.length != floatCount) outScratch = new float[floatCount];
        int received = st_receiveSamples(stHandle, outScratch, available);

        if (received > 0) {
            int samples = received * inputFormat.channelCount;
            if (inputFormat.encoding == C.ENCODING_PCM_16BIT) {
                ByteBuffer out = obtainOutput(samples * 2);
                for (int i = 0; i < samples; i++) {
                    float v = outScratch[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    out.putShort((short) (v * 32767f));
                }
                out.flip();
                outputBuffer = out;
            } else {
                int byteCount = samples * 4;
                ByteBuffer out = obtainOutput(byteCount);
                out.asFloatBuffer().put(outScratch, 0, samples);
                out.position(byteCount);
                out.flip();
                outputBuffer = out;
            }
        }
    }

    private ByteBuffer reusable = EMPTY_BUFFER;

    /** BaseAudioProcessor.replaceOutputBuffer와 같은 재사용 규칙 — 버퍼마다 새로 할당하지 않음 */
    private ByteBuffer obtainOutput(int bytes) {
        if (reusable.capacity() < bytes) {
            reusable = ByteBuffer.allocateDirect(bytes).order(ByteOrder.nativeOrder());
        } else {
            reusable.clear();
        }
        return reusable;
    }
}
