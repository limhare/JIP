package com.instplayer.app;

import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.C;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Pass-through AudioProcessor placed AFTER SoundTouch in the ExoPlayer chain.
 * Taps the exact PCM being sent to the audio device (pitch/tempo applied)
 * into a 16-bit WAV file — the Android equivalent of PC loopback recording.
 * Controlled via static startTap/stopTap so C# can drive it over JNI.
 */
public class PcmTapProcessor implements AudioProcessor {

    private static final Object LOCK = new Object();
    private static java.io.RandomAccessFile tapFile; // guarded by LOCK
    private static String tapPath = "";
    private static long tapBytes = 0;
    private static int tapSampleRate = 44100;
    private static int tapChannels = 2;

    private static volatile int currentSampleRate = 44100;
    private static volatile int currentChannels = 2;

    private AudioFormat format = AudioFormat.NOT_SET;
    private ByteBuffer outputBuffer = EMPTY_BUFFER;
    private boolean inputEnded = false;

    // ── Static tap control (called from C# via JNI) ──

    /** Start writing the played stream to a WAV file. */
    public static boolean startTap(String path) {
        synchronized (LOCK) {
            try {
                finishTapLocked();
                tapFile = new java.io.RandomAccessFile(path, "rw");
                tapFile.setLength(0);
                tapFile.write(new byte[44]); // placeholder header
                tapPath = path;
                tapBytes = 0;
                tapSampleRate = currentSampleRate;
                tapChannels = currentChannels;
                return true;
            } catch (Exception e) {
                tapFile = null;
                return false;
            }
        }
    }

    /** Stop and finalize the WAV. Returns the file path, or "" if nothing was written. */
    public static String stopTap() {
        synchronized (LOCK) {
            String path = tapPath;
            long bytes = tapBytes;
            finishTapLocked();
            return bytes > 0 ? path : "";
        }
    }

    private static void finishTapLocked() {
        if (tapFile == null) return;
        try {
            tapFile.seek(0);
            int byteRate = tapSampleRate * tapChannels * 2;
            ByteBuffer h = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
            h.put("RIFF".getBytes()); h.putInt((int) (36 + tapBytes)); h.put("WAVE".getBytes());
            h.put("fmt ".getBytes()); h.putInt(16); h.putShort((short) 1);
            h.putShort((short) tapChannels); h.putInt(tapSampleRate); h.putInt(byteRate);
            h.putShort((short) (tapChannels * 2)); h.putShort((short) 16);
            h.put("data".getBytes()); h.putInt((int) tapBytes);
            tapFile.write(h.array());
            tapFile.close();
        } catch (Exception ignored) {
        }
        tapFile = null;
    }

    // ── AudioProcessor (pass-through) ──

    @Override
    public AudioFormat configure(AudioFormat inputAudioFormat) throws UnhandledAudioFormatException {
        if (inputAudioFormat.encoding != C.ENCODING_PCM_FLOAT) {
            throw new UnhandledAudioFormatException(inputAudioFormat);
        }
        format = inputAudioFormat;
        currentSampleRate = inputAudioFormat.sampleRate;
        currentChannels = inputAudioFormat.channelCount;
        return inputAudioFormat;
    }

    @Override
    public boolean isActive() {
        return format != AudioFormat.NOT_SET;
    }

    @Override
    public void queueInput(ByteBuffer buffer) {
        int bytes = buffer.remaining();
        if (bytes == 0) return;

        // 탭 기록 (float32 → 16-bit PCM)
        synchronized (LOCK) {
            if (tapFile != null) {
                ByteBuffer dup = buffer.duplicate().order(buffer.order());
                int floats = bytes / 4;
                byte[] outB = new byte[floats * 2];
                java.nio.FloatBuffer fb = dup.asFloatBuffer();
                for (int i = 0; i < floats; i++) {
                    float v = fb.get();
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    int s = Math.round(v * 32767f);
                    outB[i * 2] = (byte) (s & 0xFF);
                    outB[i * 2 + 1] = (byte) ((s >> 8) & 0xFF);
                }
                try {
                    tapFile.write(outB);
                    tapBytes += outB.length;
                } catch (Exception ignored) {
                }
            }
        }

        // pass-through
        ByteBuffer out = ByteBuffer.allocateDirect(bytes).order(ByteOrder.nativeOrder());
        out.put(buffer);
        out.flip();
        outputBuffer = out;
    }

    @Override
    public void queueEndOfStream() {
        inputEnded = true;
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
    }

    @Override
    public void reset() {
        flush();
        format = AudioFormat.NOT_SET;
    }
}
