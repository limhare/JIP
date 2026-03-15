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
        // We only handle PCM_FLOAT; ExoPlayer will resample if needed
        if (encoding != C.ENCODING_PCM_FLOAT) {
            throw new UnhandledAudioFormatException(inputAudioFormat);
        }
        if (!inputAudioFormat.equals(this.inputFormat)) {
            this.inputFormat = inputAudioFormat;
            this.outputFormat = inputAudioFormat; // output same format
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
        // Each float = 4 bytes
        int floatCount = bytesAvail / 4;

        float[] samples = new float[floatCount];
        buffer.asFloatBuffer().get(samples);
        buffer.position(buffer.position() + bytesAvail);

        int samplesPerChannel = floatCount / inputFormat.channelCount;
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
            int byteCount = received * inputFormat.channelCount * 4;
            outputBuffer = ByteBuffer.allocateDirect(byteCount).order(ByteOrder.nativeOrder());
            outputBuffer.asFloatBuffer().put(outFloats, 0, received * inputFormat.channelCount);
            outputBuffer.limit(byteCount);
        }
    }
}
