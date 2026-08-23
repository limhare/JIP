package com.instplayer.app;

import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.C;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * ExoPlayer AudioProcessor that uses SoundTouch for high-quality pitch/tempo shifting.
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

    private volatile float pendingPitch = 0f;   // semitones
    private volatile float pendingTempo = 0f;   // percent (0 = 100% speed)

    private ByteBuffer inputBuffer  = EMPTY_BUFFER;
    private ByteBuffer outputBuffer = EMPTY_BUFFER;
    private boolean    inputEnded   = false;

    private static final int BUFFER_SIZE = 8192 * 4; // floats * bytes

    // --- Public control API ---

    public synchronized void setPitch(float semitones) {
        pendingPitch = semitones;
        if (stHandle != 0) st_setPitchSemiTones(stHandle, semitones);
    }

    public synchronized void setTempo(float tempoPercent) {
        pendingTempo = tempoPercent;
        if (stHandle != 0) st_setTempoChange(stHandle, tempoPercent);
    }

    // --- AudioProcessor interface ---

    @Override
    public AudioFormat configure(AudioFormat inputAudioFormat) throws UnhandledAudioFormatException {
        int encoding = inputAudioFormat.encoding;
        // 일반 음원은 16-bit로 들어옴 — float로 변환해 처리, 출력은 항상 float
        if (encoding != C.ENCODING_PCM_FLOAT && encoding != C.ENCODING_PCM_16BIT) {
            throw new UnhandledAudioFormatException(inputAudioFormat);
        }
        if (!inputAudioFormat.equals(this.inputFormat)) {
            this.inputFormat = inputAudioFormat;
            // 출력 형식은 입력과 동일하게 유지 — 싱크 내부 후속 프로세서(무음스킵 등)가 16-bit만 받음
            this.outputFormat = inputAudioFormat;
            recreateSoundTouch();
        }
        return outputFormat;
    }

    @Override
    public boolean isActive() {
        return inputFormat != AudioFormat.NOT_SET;
    }

    @Override
    public void queueInput(ByteBuffer buffer) {
        if (stHandle == 0 || !buffer.hasRemaining()) return;

        int bytesAvail = buffer.remaining();
        float[] samples;
        if (inputFormat.encoding == C.ENCODING_PCM_16BIT) {
            int shortCount = bytesAvail / 2;
            samples = new float[shortCount];
            java.nio.ShortBuffer sb = buffer.asShortBuffer();
            for (int i = 0; i < shortCount; i++) samples[i] = sb.get(i) / 32768f;
        } else {
            int floatCount = bytesAvail / 4;
            samples = new float[floatCount];
            buffer.asFloatBuffer().get(samples);
        }
        buffer.position(buffer.position() + bytesAvail);

        int samplesPerChannel = samples.length / inputFormat.channelCount;
        st_putSamples(stHandle, samples, samplesPerChannel);

        drainToOutputBuffer();
    }

    @Override
    public void queueEndOfStream() {
        inputEnded = true;
        if (stHandle != 0) st_flush(stHandle);
        drainToOutputBuffer();
    }

    @Override
    public ByteBuffer getOutput() {
        ByteBuffer out = outputBuffer;
        outputBuffer = EMPTY_BUFFER;
        return out;
    }

    @Override
    public boolean isEnded() {
        return inputEnded && outputBuffer == EMPTY_BUFFER;
    }

    @Override
    public void flush() {
        inputEnded = false;
        outputBuffer = EMPTY_BUFFER;
        if (stHandle != 0) st_clear(stHandle);
    }

    @Override
    public void reset() {
        flush();
        if (stHandle != 0) {
            st_destroyInstance(stHandle);
            stHandle = 0;
        }
        inputFormat  = AudioFormat.NOT_SET;
        outputFormat = AudioFormat.NOT_SET;
    }

    // --- Private helpers ---

    private void recreateSoundTouch() {
        if (stHandle != 0) st_destroyInstance(stHandle);
        stHandle = st_createInstance();
        st_setSampleRate(stHandle, inputFormat.sampleRate);
        st_setChannels(stHandle, inputFormat.channelCount);
        st_setPitchSemiTones(stHandle, pendingPitch);
        st_setTempoChange(stHandle, pendingTempo);
    }

    private void drainToOutputBuffer() {
        if (stHandle == 0) return;

        int available = st_numSamples(stHandle);
        if (available <= 0) return;

        int floatCount = available * inputFormat.channelCount;
        float[] outFloats = new float[floatCount];
        int received = st_receiveSamples(stHandle, outFloats, available);

        if (received > 0) {
            int samples = received * inputFormat.channelCount;
            if (inputFormat.encoding == C.ENCODING_PCM_16BIT) {
                int byteCount = samples * 2;
                ByteBuffer out = ByteBuffer.allocateDirect(byteCount).order(ByteOrder.nativeOrder());
                for (int i = 0; i < samples; i++) {
                    float v = outFloats[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    out.putShort((short) (v * 32767f));
                }
                out.flip();
                outputBuffer = out;
            } else {
                int byteCount = samples * 4;
                outputBuffer = ByteBuffer.allocateDirect(byteCount).order(ByteOrder.nativeOrder());
                outputBuffer.asFloatBuffer().put(outFloats, 0, samples);
                outputBuffer.limit(byteCount);
            }
        }
    }
}
