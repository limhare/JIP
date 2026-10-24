package com.instplayer.app;

import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.C;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Pass-through AudioProcessor placed at the END of the ExoPlayer chain
 * (after SoundTouch pitch and Sonic speed). While recording, it copies the
 * exact PCM sent to the audio device into a 16-bit WAV file — the Android
 * equivalent of PC loopback recording. Controlled via static startTap/stopTap
 * so C# can drive it over JNI.
 */
public class PcmTapProcessor implements AudioProcessor {

    private static final Object LOCK = new Object();
    // guarded by LOCK
    private static java.io.BufferedOutputStream tapOut;
    private static String tapPath = "";
    private static long tapBytes = 0;
    private static int tapSampleRate = 0;   // 첫 오디오가 기록될 때 확정
    private static int tapChannels = 0;
    private static boolean tapFormatMismatch = false;

    private AudioFormat format = AudioFormat.NOT_SET;
    private AudioFormat pendingFormat = AudioFormat.NOT_SET;
    private ByteBuffer outputBuffer = EMPTY_BUFFER;
    private ByteBuffer reusable = EMPTY_BUFFER;
    private boolean inputEnded = false;
    private byte[] convScratch = new byte[0];

    // ── Static tap control (called from C# via JNI) ──

    /** Start writing the played stream to a WAV file. */
    public static boolean startTap(String path) {
        synchronized (LOCK) {
            try {
                finishTapLocked();
                tapOut = new java.io.BufferedOutputStream(new java.io.FileOutputStream(path, false), 64 * 1024);
                tapOut.write(new byte[44]); // placeholder header
                tapPath = path;
                tapBytes = 0;
                tapSampleRate = 0;
                tapChannels = 0;
                tapFormatMismatch = false;
                return true;
            } catch (Exception e) {
                tapOut = null;
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
        if (tapOut == null) return;
        try {
            tapOut.flush();
            tapOut.close();
        } catch (Exception ignored) {
        }
        tapOut = null;
        int rate = tapSampleRate > 0 ? tapSampleRate : 44100;
        int ch = tapChannels > 0 ? tapChannels : 2;
        try (java.io.RandomAccessFile raf = new java.io.RandomAccessFile(tapPath, "rw")) {
            int byteRate = rate * ch * 2;
            ByteBuffer h = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
            h.put("RIFF".getBytes()); h.putInt((int) (36 + tapBytes)); h.put("WAVE".getBytes());
            h.put("fmt ".getBytes()); h.putInt(16); h.putShort((short) 1);
            h.putShort((short) ch); h.putInt(rate); h.putInt(byteRate);
            h.putShort((short) (ch * 2)); h.putShort((short) 16);
            h.put("data".getBytes()); h.putInt((int) tapBytes);
            raf.seek(0);
            raf.write(h.array());
        } catch (Exception ignored) {
        }
    }

    // ── AudioProcessor (pass-through) ──

    @Override
    public AudioFormat configure(AudioFormat inputAudioFormat) throws UnhandledAudioFormatException {
        if (inputAudioFormat.encoding != C.ENCODING_PCM_FLOAT
                && inputAudioFormat.encoding != C.ENCODING_PCM_16BIT) {
            throw new UnhandledAudioFormatException(inputAudioFormat);
        }
        pendingFormat = inputAudioFormat; // flush()에서 적용
        return inputAudioFormat;
    }

    @Override
    public boolean isActive() {
        return pendingFormat != AudioFormat.NOT_SET || format != AudioFormat.NOT_SET;
    }

    @Override
    public void queueInput(ByteBuffer buffer) {
        int bytes = buffer.remaining();
        if (bytes == 0) return;

        synchronized (LOCK) {
            if (tapOut != null && !tapFormatMismatch) {
                if (tapSampleRate == 0) {
                    tapSampleRate = format.sampleRate;
                    tapChannels = format.channelCount;
                } else if (tapSampleRate != format.sampleRate || tapChannels != format.channelCount) {
                    // 녹음 도중 형식이 바뀜(다른 샘플레이트 곡) — 헤더와 안 맞는 데이터로 오염시키지 않고 중단
                    tapFormatMismatch = true;
                }
                if (!tapFormatMismatch) {
                    try {
                        ByteBuffer dup = buffer.duplicate().order(buffer.order());
                        if (format.encoding == C.ENCODING_PCM_FLOAT) {
                            int floats = bytes / 4;
                            if (convScratch.length < floats * 2) convScratch = new byte[floats * 2];
                            java.nio.FloatBuffer fb = dup.asFloatBuffer();
                            for (int i = 0; i < floats; i++) {
                                float v = fb.get();
                                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                                int s = Math.round(v * 32767f);
                                convScratch[i * 2] = (byte) (s & 0xFF);
                                convScratch[i * 2 + 1] = (byte) ((s >> 8) & 0xFF);
                            }
                            tapOut.write(convScratch, 0, floats * 2);
                            tapBytes += floats * 2;
                        } else {
                            if (convScratch.length < bytes) convScratch = new byte[bytes];
                            dup.get(convScratch, 0, bytes);
                            tapOut.write(convScratch, 0, bytes);
                            tapBytes += bytes;
                        }
                    } catch (Exception ignored) {
                    }
                }
            }
        }

        // pass-through (BaseAudioProcessor와 같은 방식으로 출력 버퍼 재사용)
        if (reusable.capacity() < bytes) {
            reusable = ByteBuffer.allocateDirect(bytes).order(ByteOrder.nativeOrder());
        } else {
            reusable.clear();
        }
        reusable.put(buffer);
        reusable.flip();
        outputBuffer = reusable;
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
        if (pendingFormat != AudioFormat.NOT_SET) format = pendingFormat;
    }

    @Override
    public void reset() {
        flush();
        format = AudioFormat.NOT_SET;
        pendingFormat = AudioFormat.NOT_SET;
        reusable = EMPTY_BUFFER;
    }
}
