package com.instplayer.app;

import android.content.Context;
import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.net.Uri;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;

/**
 * Demucs accompaniment extractor.
 * Pipeline: input audio → MediaCodec PCM → [JNI demucs_jni] Eigen inference → accompaniment.wav
 *
 * Usage:
 *   1. init(modelPath)          — load model once (blocking, call from bg thread)
 *   2. startSeparate(input, outDir) — start background separation
 *   3. poll isDone() / getProgress() / getResult() / getError()
 *   4. destroy() when done with the processor
 */
public class DemucsProcessor {

    private static final String TAG        = "JIP_DEMUCS";
    private static final int    TIMEOUT_US = 10_000;

    static {
        System.loadLibrary("demucs_jni");
    }

    // JNI native methods
    static native long    dmCreate(String modelPath);
    static native void    dmSeparatePCM(long handle, float[] left, float[] right,
                                         int sampleRate, String outputDir);
    static native int     dmGetProgress(long handle);
    static native boolean dmIsDone(long handle);
    static native String  dmGetError(long handle);
    static native String  dmGetResult(long handle);
    static native void    dmDestroy(long handle);

    // ── State ────────────────────────────────────────────────────────────────
    private final Context context;
    private long          modelHandle = 0;
    private volatile int     progress  = 0;
    private volatile boolean done      = false;
    private volatile String  result    = null;
    private volatile String  error     = null;
    private volatile boolean cancelled = false;
    private volatile Thread  workerThread = null;

    public DemucsProcessor(Context context) {
        this.context = context;
    }

    public int     getProgress() { return progress; }
    public boolean isDone()      { return done;     }
    public String  getResult()   { return result;   }
    public String  getError()    { return error;    }

    /** Load model (blocking). Returns true on success. */
    public boolean init(String modelPath) {
        if (modelHandle != 0) return true;  // already loaded
        Log.i(TAG, "init: loading model " + modelPath);
        modelHandle = dmCreate(modelPath);
        String err = dmGetError(modelHandle);
        if (err != null && !err.isEmpty()) {
            Log.e(TAG, "Model load error: " + err);
            dmDestroy(modelHandle);
            modelHandle = 0;
            error = err;
            return false;
        }
        Log.i(TAG, "init: model loaded OK");
        return true;
    }

    /** Start accompaniment separation in background thread. */
    public void startSeparate(final String inputPath, final String outputDir) {
        progress  = 0;
        done      = false;
        result    = null;
        error     = null;
        cancelled = false;

        final long handle = modelHandle;
        if (handle == 0) {
            error = "Model not loaded";
            done  = true;
            return;
        }

        workerThread = new Thread(new Runnable() {
            @Override public void run() {
                try {
                    doSeparate(handle, inputPath, outputDir);
                } catch (Exception e) {
                    Log.e(TAG, "Separation failed", e);
                    error = e.getMessage() != null ? e.getMessage() : "Unknown error";
                    done  = true;
                }
            }
        }, "jip-demucs");
        workerThread.start();
    }

    public void cancel() { cancelled = true; }

    /** Free native resources. */
    public void destroy() {
        if (modelHandle != 0) {
            dmDestroy(modelHandle);
            modelHandle = 0;
        }
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void doSeparate(long handle, String inputPath, String outputDir) throws Exception {

        // ── 1. MediaExtractor ──────────────────────────────────────────────
        MediaExtractor extractor = new MediaExtractor();
        try {
            if (inputPath.startsWith("content://")) {
                extractor.setDataSource(context, Uri.parse(inputPath), null);
            } else {
                extractor.setDataSource(inputPath);
            }
        } catch (IOException e) {
            throw new Exception("Cannot open: " + e.getMessage());
        }

        int audioTrack = -1;
        MediaFormat format = null;
        for (int i = 0; i < extractor.getTrackCount(); i++) {
            MediaFormat f = extractor.getTrackFormat(i);
            String m = f.getString(MediaFormat.KEY_MIME);
            if (m != null && m.startsWith("audio/")) {
                audioTrack = i;
                format = f;
                break;
            }
        }
        if (audioTrack < 0 || format == null) {
            extractor.release();
            throw new Exception("No audio track found");
        }
        extractor.selectTrack(audioTrack);

        int sampleRate  = format.getInteger(MediaFormat.KEY_SAMPLE_RATE);
        int channels    = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
        long durationUs = format.containsKey(MediaFormat.KEY_DURATION)
                        ? format.getLong(MediaFormat.KEY_DURATION) : -1;

        Log.i(TAG, String.format("Decode: sr=%d ch=%d dur=%ds",
              sampleRate, channels, durationUs / 1_000_000L));

        // ── 2. MediaCodec decoder ──────────────────────────────────────────
        String mime = format.getString(MediaFormat.KEY_MIME);
        MediaCodec decoder;
        try {
            decoder = MediaCodec.createDecoderByType(mime);
        } catch (IOException e) {
            extractor.release();
            throw new Exception("No decoder for " + mime);
        }
        decoder.configure(format, null, null, 0);
        decoder.start();

        // Accumulate PCM chunks (interleaved, converted to float32)
        List<float[]> chunks   = new ArrayList<>();
        int           totalPCM = 0;  // total interleaved samples (ch * frames)

        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        boolean inputDone  = false;
        boolean outputDone = false;
        boolean isFloat    = false;

        while (!outputDone && !cancelled) {
            if (!inputDone) {
                int idx = decoder.dequeueInputBuffer(TIMEOUT_US);
                if (idx >= 0) {
                    ByteBuffer inBuf = decoder.getInputBuffer(idx);
                    int size = extractor.readSampleData(inBuf, 0);
                    if (size < 0) {
                        decoder.queueInputBuffer(idx, 0, 0, 0,
                                MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        inputDone = true;
                    } else {
                        decoder.queueInputBuffer(idx, 0, size,
                                extractor.getSampleTime(), 0);
                        extractor.advance();
                    }
                }
            }

            int idx = decoder.dequeueOutputBuffer(info, TIMEOUT_US);
            if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                MediaFormat f = decoder.getOutputFormat();
                int enc = f.containsKey(MediaFormat.KEY_PCM_ENCODING)
                        ? f.getInteger(MediaFormat.KEY_PCM_ENCODING)
                        : android.media.AudioFormat.ENCODING_PCM_16BIT;
                isFloat = (enc == android.media.AudioFormat.ENCODING_PCM_FLOAT);
                if (f.containsKey(MediaFormat.KEY_SAMPLE_RATE))
                    sampleRate = f.getInteger(MediaFormat.KEY_SAMPLE_RATE);
                if (f.containsKey(MediaFormat.KEY_CHANNEL_COUNT))
                    channels = f.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
                Log.i(TAG, "PCM: " + (isFloat ? "float32" : "int16") +
                      " sr=" + sampleRate + " ch=" + channels);

            } else if (idx >= 0) {
                ByteBuffer out = decoder.getOutputBuffer(idx);
                if (out != null && info.size > 0) {
                    out.position(info.offset);
                    out.limit(info.offset + info.size);
                    out.order(ByteOrder.LITTLE_ENDIAN);

                    float[] chunk;
                    if (isFloat) {
                        int n = info.size / 4;
                        chunk = new float[n];
                        for (int i = 0; i < n; i++) chunk[i] = out.getFloat();
                    } else {
                        int n = info.size / 2;
                        chunk = new float[n];
                        for (int i = 0; i < n; i++)
                            chunk[i] = out.getShort() / 32768.0f;
                    }
                    chunks.add(chunk);
                    totalPCM += chunk.length;
                }
                decoder.releaseOutputBuffer(idx, false);

                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0)
                    outputDone = true;

                // Decode phase = 0–5% of overall progress
                if (durationUs > 0 && info.presentationTimeUs > 0)
                    progress = (int) Math.min(5,
                            info.presentationTimeUs * 5L / durationUs);
            }
        }

        decoder.stop();
        decoder.release();
        extractor.release();

        if (cancelled) {
            error = "Cancelled";
            done  = true;
            return;
        }

        // ── 3. Assemble interleaved PCM ────────────────────────────────────
        float[] pcm = new float[totalPCM];
        int pos = 0;
        for (float[] chunk : chunks) {
            System.arraycopy(chunk, 0, pcm, pos, chunk.length);
            pos += chunk.length;
        }
        chunks.clear();

        int frames = totalPCM / channels;
        Log.i(TAG, "Decoded frames=" + frames + " sr=" + sampleRate + " ch=" + channels);

        // ── 4. De-interleave → left / right ───────────────────────────────
        float[] leftPCM  = new float[frames];
        float[] rightPCM = new float[frames];
        if (channels == 1) {
            System.arraycopy(pcm, 0, leftPCM,  0, frames);
            System.arraycopy(pcm, 0, rightPCM, 0, frames);
        } else {
            for (int i = 0; i < frames; i++) {
                leftPCM[i]  = pcm[i * 2];
                rightPCM[i] = pcm[i * 2 + 1];
            }
        }
        pcm = null;  // free memory

        // Make sure outputDir exists
        new java.io.File(outputDir).mkdirs();

        // ── 5. JNI inference (blocking, progress 5–100 via JNI callback) ──
        // Start a polling thread to bridge JNI progress → Java progress field
        final long h = handle;
        Thread pollThread = new Thread(new Runnable() {
            @Override public void run() {
                while (!dmIsDone(h) && !cancelled) {
                    int p = dmGetProgress(h);
                    // Map JNI 0–100 → Java 5–100
                    progress = 5 + p * 95 / 100;
                    try { Thread.sleep(300); } catch (InterruptedException e) { return; }
                }
            }
        }, "jip-demucs-poll");
        pollThread.setDaemon(true);
        pollThread.start();

        dmSeparatePCM(handle, leftPCM, rightPCM, sampleRate, outputDir);

        pollThread.interrupt();

        if (cancelled) {
            error = "Cancelled";
            done  = true;
            return;
        }

        // ── 6. Read result ─────────────────────────────────────────────────
        String jniError = dmGetError(handle);
        if (jniError != null && !jniError.isEmpty()) {
            error = jniError;
            done  = true;
            return;
        }

        String wavPath = dmGetResult(handle);
        if (wavPath == null || wavPath.isEmpty()) {
            error = "No output from inference";
            done  = true;
            return;
        }

        progress = 100;
        result   = wavPath;
        done     = true;
        Log.i(TAG, "Done: " + wavPath);
    }
}
