package com.instplayer.app;

import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Exports audio file to MP3 with pitch/tempo transformation.
 * Pipeline: MediaExtractor → MediaCodec → PCM float → [JNI] SoundTouch + LAME → .mp3
 * State is polled by C# via getProgress/isDone/getResult/getError.
 */
public class AudioExporter {

    private static final String TAG = "JIP_EXPORT";
    private static final int TIMEOUT_US = 10000;
    private static final int BLOCK_FRAMES = 8192;

    static {
        System.loadLibrary("jip_export");
        System.loadLibrary("rb_export");
    }

    // JNI native methods (SoundTouch - standard export)
    native long  je_create(int sampleRate, int channels, float pitchSemitones, float tempoPercent, String outputPath);
    native void  je_feed(long handle, float[] pcm, int frameCount);
    native void  je_flush(long handle);
    native void  je_destroy(long handle);

    // JNI native methods (Rubber Band - HQ export)
    native long  rb_create(int sampleRate, int channels, float pitchSemitones, float tempoPercent, String outputPath);
    native void  rb_study(long handle, float[] pcm, int frameCount, boolean isFinal);
    native void  rb_process(long handle, float[] pcm, int frameCount, boolean isFinal);
    native void  rb_flush(long handle);
    native void  rb_destroy(long handle);

    // State polled by C#
    private volatile int     progress  = 0;
    private volatile boolean done      = false;
    private volatile String  result    = null;  // output path on success
    private volatile String  error     = null;  // error message on failure
    private volatile boolean cancelled = false;

    public int     getProgress() { return progress; }
    public boolean isDone()      { return done;     }
    public String  getResult()   { return result;   }
    public String  getError()    { return error;    }

    /** Start export in background thread. Call isDone() to poll. */
    public void startExport(String inputPath, float pitchSemitones, float tempoPercent, String outputPath) {
        progress  = 0;
        done      = false;
        result    = null;
        error     = null;
        cancelled = false;

        final String fInput  = inputPath;
        final float  fPitch  = pitchSemitones;
        final float  fTempo  = tempoPercent;
        final String fOutput = outputPath;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    doExport(fInput, fPitch, fTempo, fOutput);
                } catch (Exception e) {
                    Log.e(TAG, "Export failed", e);
                    error = e.getMessage() != null ? e.getMessage() : "Unknown error";
                    done  = true;
                }
            }
        }, "jip-export").start();
    }

    public void cancel() { cancelled = true; }

    /** Start HQ (Rubber Band) export in background thread. Call isDone() to poll. */
    public void startExportHQ(String inputPath, float pitchSemitones, float tempoPercent, String outputPath) {
        progress  = 0;
        done      = false;
        result    = null;
        error     = null;
        cancelled = false;

        final String fInput  = inputPath;
        final float  fPitch  = pitchSemitones;
        final float  fTempo  = tempoPercent;
        final String fOutput = outputPath;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    doExportHQ(fInput, fPitch, fTempo, fOutput);
                } catch (Exception e) {
                    Log.e(TAG, "HQ Export failed", e);
                    error = e.getMessage() != null ? e.getMessage() : "Unknown error";
                    done  = true;
                }
            }
        }, "jip-export-hq").start();
    }

    private void doExportHQ(String inputPath, float pitchSemitones, float tempoPercent,
                            String outputPath) throws Exception {

        Log.i(TAG, "HQ Export start (Rubber Band): " + inputPath);

        // ---- Common: extract audio info ----
        MediaExtractor extractor = new MediaExtractor();
        try { extractor.setDataSource(inputPath); }
        catch (IOException e) { throw new Exception("Cannot open: " + e.getMessage()); }

        int audioTrack = -1;
        MediaFormat format = null;
        for (int i = 0; i < extractor.getTrackCount(); i++) {
            MediaFormat f = extractor.getTrackFormat(i);
            String m = f.getString(MediaFormat.KEY_MIME);
            if (m != null && m.startsWith("audio/")) { audioTrack = i; format = f; break; }
        }
        if (audioTrack < 0 || format == null) { extractor.release(); throw new Exception("No audio track"); }
        extractor.selectTrack(audioTrack);

        String mime = format.getString(MediaFormat.KEY_MIME);
        int sampleRate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE);
        int channels   = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
        long durationUs = format.containsKey(MediaFormat.KEY_DURATION) ? format.getLong(MediaFormat.KEY_DURATION) : -1;

        Log.i(TAG, String.format("HQ: %s sr=%d ch=%d dur=%ds P=%.1f T=%.1f",
              mime, sampleRate, channels, durationUs / 1_000_000L, pitchSemitones, tempoPercent));

        // ---- Create Rubber Band handle ----
        long handle = rb_create(sampleRate, channels, pitchSemitones, tempoPercent, outputPath);
        if (handle == 0) { extractor.release(); throw new Exception("rb_create failed"); }

        float[] block = new float[BLOCK_FRAMES * channels];

        // ==================== PASS 1: STUDY ====================
        {
            MediaCodec decoder = MediaCodec.createDecoderByType(mime);
            decoder.configure(format, null, null, 0);
            decoder.start();

            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            boolean inputDone = false, outputDone = false, isFloat = false;
            int blockFill = 0;

            while (!outputDone && !cancelled) {
                if (!inputDone) {
                    int idx = decoder.dequeueInputBuffer(TIMEOUT_US);
                    if (idx >= 0) {
                        ByteBuffer inBuf = decoder.getInputBuffer(idx);
                        int size = extractor.readSampleData(inBuf, 0);
                        if (size < 0) {
                            decoder.queueInputBuffer(idx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                            inputDone = true;
                        } else {
                            decoder.queueInputBuffer(idx, 0, size, extractor.getSampleTime(), 0);
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
                } else if (idx >= 0) {
                    ByteBuffer out = decoder.getOutputBuffer(idx);
                    if (out != null && info.size > 0) {
                        out.position(info.offset); out.limit(info.offset + info.size);
                        out.order(ByteOrder.LITTLE_ENDIAN);
                        if (isFloat) {
                            int totalFrames = info.size / 4 / channels;
                            int pos = 0;
                            while (pos < totalFrames) {
                                int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                                for (int i = 0; i < take * channels; i++) block[blockFill * channels + i] = out.getFloat();
                                blockFill += take; pos += take;
                                if (blockFill == BLOCK_FRAMES) { rb_study(handle, block, BLOCK_FRAMES, false); blockFill = 0; }
                            }
                        } else {
                            int totalFrames = info.size / 2 / channels;
                            int pos = 0;
                            while (pos < totalFrames) {
                                int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                                for (int i = 0; i < take * channels; i++) block[blockFill * channels + i] = out.getShort() / 32768.0f;
                                blockFill += take; pos += take;
                                if (blockFill == BLOCK_FRAMES) { rb_study(handle, block, BLOCK_FRAMES, false); blockFill = 0; }
                            }
                        }
                    }
                    decoder.releaseOutputBuffer(idx, false);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) outputDone = true;
                    if (durationUs > 0 && info.presentationTimeUs > 0)
                        progress = (int) Math.min(49, info.presentationTimeUs * 50L / durationUs);
                }
            }
            if (!cancelled && blockFill > 0) rb_study(handle, block, blockFill, true);
            else if (!cancelled) rb_study(handle, new float[0], 0, true);
            decoder.stop(); decoder.release();
        }

        if (cancelled) { rb_destroy(handle); extractor.release(); new java.io.File(outputPath).delete(); error = "Cancelled"; done = true; return; }

        // ==================== PASS 2: PROCESS ====================
        {
            // Reset extractor to beginning
            extractor.seekTo(0, MediaExtractor.SEEK_TO_CLOSEST_SYNC);

            MediaCodec decoder = MediaCodec.createDecoderByType(mime);
            decoder.configure(format, null, null, 0);
            decoder.start();

            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            boolean inputDone = false, outputDone = false, isFloat = false;
            int blockFill = 0;

            while (!outputDone && !cancelled) {
                if (!inputDone) {
                    int idx = decoder.dequeueInputBuffer(TIMEOUT_US);
                    if (idx >= 0) {
                        ByteBuffer inBuf = decoder.getInputBuffer(idx);
                        int size = extractor.readSampleData(inBuf, 0);
                        if (size < 0) {
                            decoder.queueInputBuffer(idx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                            inputDone = true;
                        } else {
                            decoder.queueInputBuffer(idx, 0, size, extractor.getSampleTime(), 0);
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
                } else if (idx >= 0) {
                    ByteBuffer out = decoder.getOutputBuffer(idx);
                    if (out != null && info.size > 0) {
                        out.position(info.offset); out.limit(info.offset + info.size);
                        out.order(ByteOrder.LITTLE_ENDIAN);
                        if (isFloat) {
                            int totalFrames = info.size / 4 / channels;
                            int pos = 0;
                            while (pos < totalFrames) {
                                int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                                for (int i = 0; i < take * channels; i++) block[blockFill * channels + i] = out.getFloat();
                                blockFill += take; pos += take;
                                if (blockFill == BLOCK_FRAMES) { rb_process(handle, block, BLOCK_FRAMES, false); blockFill = 0; }
                            }
                        } else {
                            int totalFrames = info.size / 2 / channels;
                            int pos = 0;
                            while (pos < totalFrames) {
                                int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                                for (int i = 0; i < take * channels; i++) block[blockFill * channels + i] = out.getShort() / 32768.0f;
                                blockFill += take; pos += take;
                                if (blockFill == BLOCK_FRAMES) { rb_process(handle, block, BLOCK_FRAMES, false); blockFill = 0; }
                            }
                        }
                    }
                    decoder.releaseOutputBuffer(idx, false);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) outputDone = true;
                    if (durationUs > 0 && info.presentationTimeUs > 0)
                        progress = (int) Math.min(99, 50 + info.presentationTimeUs * 50L / durationUs);
                }
            }
            if (!cancelled && blockFill > 0) rb_process(handle, block, blockFill, true);
            else if (!cancelled) rb_process(handle, new float[0], 0, true);
            decoder.stop(); decoder.release();
        }

        extractor.release();

        if (cancelled) { rb_destroy(handle); new java.io.File(outputPath).delete(); error = "Cancelled"; done = true; return; }

        rb_flush(handle);
        rb_destroy(handle);
        progress = 100;
        result = outputPath;
        done = true;
        Log.i(TAG, "HQ Done: " + outputPath);
    }

    /** Start decode-to-WAV (16-bit PCM) in background thread. Poll isDone(). */
    public void startDecodeWav(String inputPath, String outputPath) {
        progress  = 0;
        done      = false;
        result    = null;
        error     = null;
        cancelled = false;

        final String fInput  = inputPath;
        final String fOutput = outputPath;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    doDecodeWav(fInput, fOutput);
                } catch (Exception e) {
                    Log.e(TAG, "Decode failed", e);
                    error = e.getMessage() != null ? e.getMessage() : "Unknown error";
                    done  = true;
                }
            }
        }, "jip-decode").start();
    }

    private void doDecodeWav(String inputPath, String outputPath) throws Exception {
        MediaExtractor extractor = new MediaExtractor();
        try { extractor.setDataSource(inputPath); }
        catch (IOException e) { throw new Exception("Cannot open: " + e.getMessage()); }

        int audioTrack = -1;
        MediaFormat format = null;
        for (int i = 0; i < extractor.getTrackCount(); i++) {
            MediaFormat f = extractor.getTrackFormat(i);
            String m = f.getString(MediaFormat.KEY_MIME);
            if (m != null && m.startsWith("audio/")) { audioTrack = i; format = f; break; }
        }
        if (audioTrack < 0 || format == null) { extractor.release(); throw new Exception("No audio track"); }
        extractor.selectTrack(audioTrack);

        String mime = format.getString(MediaFormat.KEY_MIME);
        int sampleRate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE);
        int channels   = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
        long durationUs = format.containsKey(MediaFormat.KEY_DURATION) ? format.getLong(MediaFormat.KEY_DURATION) : -1;

        MediaCodec decoder = MediaCodec.createDecoderByType(mime);
        decoder.configure(format, null, null, 0);
        decoder.start();

        java.io.RandomAccessFile raf = new java.io.RandomAccessFile(outputPath, "rw");
        raf.setLength(0);
        raf.write(new byte[44]); // placeholder header
        long dataBytes = 0;

        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        boolean inputDone = false, outputDone = false, isFloat = false;
        byte[] tmp = new byte[65536];

        while (!outputDone && !cancelled) {
            if (!inputDone) {
                int idx = decoder.dequeueInputBuffer(TIMEOUT_US);
                if (idx >= 0) {
                    ByteBuffer inBuf = decoder.getInputBuffer(idx);
                    int size = extractor.readSampleData(inBuf, 0);
                    if (size < 0) {
                        decoder.queueInputBuffer(idx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        inputDone = true;
                    } else {
                        decoder.queueInputBuffer(idx, 0, size, extractor.getSampleTime(), 0);
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
            } else if (idx >= 0) {
                ByteBuffer out = decoder.getOutputBuffer(idx);
                if (out != null && info.size > 0) {
                    out.position(info.offset);
                    out.limit(info.offset + info.size);
                    out.order(ByteOrder.LITTLE_ENDIAN);
                    if (isFloat) {
                        int samples = info.size / 4;
                        int need = samples * 2;
                        if (tmp.length < need) tmp = new byte[need];
                        for (int i = 0; i < samples; i++) {
                            float v = out.getFloat();
                            int s = Math.max(-32768, Math.min(32767, Math.round(v * 32767f)));
                            tmp[i * 2]     = (byte)(s & 0xFF);
                            tmp[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                        }
                        raf.write(tmp, 0, need);
                        dataBytes += need;
                    } else {
                        int n = info.size;
                        if (tmp.length < n) tmp = new byte[n];
                        out.get(tmp, 0, n);
                        raf.write(tmp, 0, n);
                        dataBytes += n;
                    }
                }
                decoder.releaseOutputBuffer(idx, false);
                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) outputDone = true;
                if (durationUs > 0 && info.presentationTimeUs > 0)
                    progress = (int) Math.min(99, info.presentationTimeUs * 100L / durationUs);
            }
        }
        decoder.stop(); decoder.release(); extractor.release();

        if (cancelled) { raf.close(); new java.io.File(outputPath).delete(); error = "Cancelled"; done = true; return; }

        raf.seek(0);
        int byteRate = sampleRate * channels * 2;
        java.nio.ByteBuffer hb = java.nio.ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
        hb.put("RIFF".getBytes()); hb.putInt((int)(36 + dataBytes)); hb.put("WAVE".getBytes());
        hb.put("fmt ".getBytes()); hb.putInt(16); hb.putShort((short)1); hb.putShort((short)channels);
        hb.putInt(sampleRate); hb.putInt(byteRate); hb.putShort((short)(channels * 2)); hb.putShort((short)16);
        hb.put("data".getBytes()); hb.putInt((int)dataBytes);
        raf.write(hb.array());
        raf.close();

        progress = 100;
        result = outputPath;
        done = true;
        Log.i(TAG, "Decode done: " + outputPath);
    }

    private void doExport(String inputPath, float pitchSemitones, float tempoPercent,
                          String outputPath) throws Exception {

        // ---- 1. MediaExtractor ----
        MediaExtractor extractor = new MediaExtractor();
        try {
            extractor.setDataSource(inputPath);
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

        String mime     = format.getString(MediaFormat.KEY_MIME);
        int sampleRate  = format.getInteger(MediaFormat.KEY_SAMPLE_RATE);
        int channels    = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
        long durationUs = format.containsKey(MediaFormat.KEY_DURATION)
                        ? format.getLong(MediaFormat.KEY_DURATION) : -1;

        Log.i(TAG, String.format("Export: %s sr=%d ch=%d dur=%ds P=%.1f T=%.1f",
              mime, sampleRate, channels, durationUs / 1_000_000L,
              pitchSemitones, tempoPercent));

        // ---- 2. MediaCodec decoder ----
        MediaCodec decoder;
        try {
            decoder = MediaCodec.createDecoderByType(mime);
        } catch (IOException e) {
            extractor.release();
            throw new Exception("No decoder for " + mime);
        }
        decoder.configure(format, null, null, 0);
        decoder.start();

        // ---- 3. JNI handle ----
        long handle = je_create(sampleRate, channels, pitchSemitones, tempoPercent, outputPath);
        if (handle == 0) {
            decoder.stop(); decoder.release(); extractor.release();
            throw new Exception("Native export init failed (je_create returned 0)");
        }

        float[] block    = new float[BLOCK_FRAMES * channels];
        int     blockFill = 0;

        // ---- 4. Decode loop ----
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
                Log.i(TAG, "PCM: " + (isFloat ? "float32" : "int16"));

            } else if (idx >= 0) {
                ByteBuffer out = decoder.getOutputBuffer(idx);
                if (out != null && info.size > 0) {
                    out.position(info.offset);
                    out.limit(info.offset + info.size);
                    out.order(ByteOrder.LITTLE_ENDIAN);

                    if (isFloat) {
                        int totalFrames = info.size / 4 / channels;
                        int pos = 0;
                        while (pos < totalFrames) {
                            int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                            for (int i = 0; i < take * channels; i++)
                                block[blockFill * channels + i] = out.getFloat();
                            blockFill += take;
                            pos       += take;
                            if (blockFill == BLOCK_FRAMES) {
                                je_feed(handle, block, BLOCK_FRAMES);
                                blockFill = 0;
                            }
                        }
                    } else {
                        int totalFrames = info.size / 2 / channels;
                        int pos = 0;
                        while (pos < totalFrames) {
                            int take = Math.min(BLOCK_FRAMES - blockFill, totalFrames - pos);
                            for (int i = 0; i < take * channels; i++)
                                block[blockFill * channels + i] = out.getShort() / 32768.0f;
                            blockFill += take;
                            pos       += take;
                            if (blockFill == BLOCK_FRAMES) {
                                je_feed(handle, block, BLOCK_FRAMES);
                                blockFill = 0;
                            }
                        }
                    }
                }
                decoder.releaseOutputBuffer(idx, false);

                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0)
                    outputDone = true;

                if (durationUs > 0 && info.presentationTimeUs > 0)
                    progress = (int) Math.min(99, info.presentationTimeUs * 100L / durationUs);
            }
        }

        if (!cancelled && blockFill > 0)
            je_feed(handle, block, blockFill);

        // ---- 5. Cleanup ----
        decoder.stop();
        decoder.release();
        extractor.release();

        if (cancelled) {
            je_destroy(handle);
            new java.io.File(outputPath).delete();
            error = "Cancelled";
            done  = true;
            return;
        }

        je_flush(handle);
        je_destroy(handle);
        progress = 100;
        result   = outputPath;
        done     = true;
        Log.i(TAG, "Done: " + outputPath);
    }
}
