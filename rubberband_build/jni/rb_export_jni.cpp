// rb_export_jni.cpp
// JNI bridge: Rubber Band (phase vocoder) + LAME MP3 export pipeline
// Java class: com.instplayer.app.AudioExporter
//
// 2-pass offline mode for highest quality:
//   Pass 1: rb_create → rb_study (전체 오디오) → rb_study_done
//   Pass 2: rb_process (전체 오디오) → LAME encode → MP3 파일
//   rb_flush → rb_destroy

#include <jni.h>
#include <android/log.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include <cmath>

// Rubber Band
#include "rubberband/RubberBandStretcher.h"

// LAME
extern "C" {
#include "lame.h"
}

#define LOG_TAG "JIP_RB_EXPORT"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

using RubberBand::RubberBandStretcher;

// --------------------------------------------------------------------------
// ExportContext — holds all state for one HQ export session
// --------------------------------------------------------------------------
struct RBExportContext {
    RubberBandStretcher* stretcher;
    lame_global_flags*   lame;
    FILE*                outFile;
    int                  channels;
    int                  sampleRate;

    // De-interleave buffers (Rubber Band expects separate channel pointers)
    std::vector<float>   chanBuf[2];      // per-channel input
    // Retrieve buffer
    std::vector<float>   retrieveBuf[2];
    // LAME interleaved output buffer
    std::vector<float>   interleavedBuf;
    // LAME MP3 output buffer
    std::vector<unsigned char> mp3Buf;

    RBExportContext() : stretcher(nullptr), lame(nullptr), outFile(nullptr),
                        channels(2), sampleRate(44100) {}
};

static const int MP3_BUF_FACTOR = 5;

// --------------------------------------------------------------------------
// Helper: encode interleaved float samples to MP3 and write to file
// --------------------------------------------------------------------------
static bool encodeLameAndWrite(RBExportContext* ctx, const float* interleaved, int frames) {
    int ch = ctx->channels;
    int mp3BufSize = frames * MP3_BUF_FACTOR + 7200;
    ctx->mp3Buf.resize(mp3BufSize);

    int encoded;
    if (ch == 1) {
        encoded = lame_encode_buffer_ieee_float(
            ctx->lame, interleaved, interleaved, frames,
            ctx->mp3Buf.data(), mp3BufSize);
    } else {
        encoded = lame_encode_buffer_interleaved_ieee_float(
            ctx->lame, interleaved, frames,
            ctx->mp3Buf.data(), mp3BufSize);
    }

    if (encoded < 0) {
        LOGE("LAME encode error: %d", encoded);
        return false;
    }
    if (encoded > 0) {
        fwrite(ctx->mp3Buf.data(), 1, encoded, ctx->outFile);
    }
    return true;
}

// --------------------------------------------------------------------------
// Helper: retrieve available samples from Rubber Band, encode and write
// --------------------------------------------------------------------------
static bool drainRBAndEncode(RBExportContext* ctx) {
    int ch = ctx->channels;
    while (ctx->stretcher->available() > 0) {
        int avail = ctx->stretcher->available();
        if (avail <= 0) break;

        // Ensure retrieve buffers are large enough
        for (int c = 0; c < ch; c++) {
            ctx->retrieveBuf[c].resize(avail);
        }
        float* rbOut[2] = { ctx->retrieveBuf[0].data(), ctx->retrieveBuf[1].data() };

        int got = (int)ctx->stretcher->retrieve(rbOut, avail);
        if (got <= 0) break;

        // Re-interleave for LAME
        ctx->interleavedBuf.resize(got * ch);
        if (ch == 1) {
            memcpy(ctx->interleavedBuf.data(), rbOut[0], got * sizeof(float));
        } else {
            for (int i = 0; i < got; i++) {
                ctx->interleavedBuf[i * 2]     = rbOut[0][i];
                ctx->interleavedBuf[i * 2 + 1] = rbOut[1][i];
            }
        }

        if (!encodeLameAndWrite(ctx, ctx->interleavedBuf.data(), got))
            return false;
    }
    return true;
}

// --------------------------------------------------------------------------
// JNI: rb_create(sampleRate, channels, pitchSemitones, tempoPercent, outputPath)
// Initializes Rubber Band in OFFLINE mode with R3 (Finer) engine
// --------------------------------------------------------------------------
extern "C" JNIEXPORT jlong JNICALL
Java_com_instplayer_app_AudioExporter_rb_1create(
        JNIEnv* env, jobject /*obj*/,
        jint sampleRate, jint channels,
        jfloat pitchSemitones, jfloat tempoPercent,
        jstring outputPath) {

    const char* path = env->GetStringUTFChars(outputPath, nullptr);
    LOGI("rb_create: sr=%d ch=%d pitch=%.1f tempo=%.1f path=%s",
         sampleRate, channels, (float)pitchSemitones, (float)tempoPercent, path);

    RBExportContext* ctx = new RBExportContext();
    ctx->channels   = (int)channels;
    ctx->sampleRate = (int)sampleRate;

    // --- Rubber Band setup (offline mode, R3 Finer engine, formant preserved) ---
    int options = RubberBandStretcher::OptionProcessOffline
                | RubberBandStretcher::OptionEngineFiner
                | RubberBandStretcher::OptionFormantPreserved;

    try {
        ctx->stretcher = new RubberBandStretcher(
            (size_t)sampleRate, (size_t)channels, options);
    } catch (...) {
        LOGE("RubberBandStretcher creation failed");
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }

    // Convert semitones to pitch scale: 2^(semitones/12)
    double pitchScale = pow(2.0, (double)pitchSemitones / 12.0);
    ctx->stretcher->setPitchScale(pitchScale);

    // Convert tempo percent offset to time ratio
    // tempoPercent: 0 = normal, 50 = 150% speed, -50 = 50% speed
    // timeRatio = 1.0 / (1.0 + tempoPercent/100.0)
    // (faster tempo = shorter time = smaller ratio)
    double timeRatio = 1.0 / (1.0 + (double)tempoPercent / 100.0);
    ctx->stretcher->setTimeRatio(timeRatio);

    LOGI("rb_create: pitchScale=%.4f timeRatio=%.4f", pitchScale, timeRatio);

    // --- LAME setup ---
    ctx->lame = lame_init();
    if (!ctx->lame) {
        LOGE("lame_init failed");
        delete ctx->stretcher;
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }
    lame_set_num_channels(ctx->lame, channels);
    lame_set_in_samplerate(ctx->lame, sampleRate);
    lame_set_brate(ctx->lame, 320);
    lame_set_quality(ctx->lame, 2);
    lame_set_mode(ctx->lame, channels == 1 ? MONO : JOINT_STEREO);
    lame_set_VBR(ctx->lame, vbr_off);

    if (lame_init_params(ctx->lame) < 0) {
        LOGE("lame_init_params failed");
        lame_close(ctx->lame);
        delete ctx->stretcher;
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }

    // --- Open output file ---
    ctx->outFile = fopen(path, "wb");
    if (!ctx->outFile) {
        LOGE("Cannot open output file: %s", path);
        lame_close(ctx->lame);
        delete ctx->stretcher;
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }

    env->ReleaseStringUTFChars(outputPath, path);
    return (jlong)(intptr_t)ctx;
}

// --------------------------------------------------------------------------
// JNI: rb_study(handle, floatArray, frameCount, isFinal)
// Pass 1: feed audio data for analysis (no output produced)
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_rb_1study(
        JNIEnv* env, jobject /*obj*/,
        jlong handle, jfloatArray pcmArray, jint frameCount, jboolean isFinal) {

    RBExportContext* ctx = (RBExportContext*)(intptr_t)handle;
    if (!ctx || !ctx->stretcher) return;

    jfloat* pcm = env->GetFloatArrayElements(pcmArray, nullptr);
    if (!pcm) return;

    int ch = ctx->channels;
    int frames = (int)frameCount;

    // De-interleave into separate channel buffers
    for (int c = 0; c < ch; c++) {
        ctx->chanBuf[c].resize(frames);
    }
    if (ch == 1) {
        memcpy(ctx->chanBuf[0].data(), pcm, frames * sizeof(float));
    } else {
        for (int i = 0; i < frames; i++) {
            ctx->chanBuf[0][i] = pcm[i * 2];
            ctx->chanBuf[1][i] = pcm[i * 2 + 1];
        }
    }

    const float* chanPtrs[2] = { ctx->chanBuf[0].data(), ctx->chanBuf[1].data() };
    ctx->stretcher->study(chanPtrs, (size_t)frames, (bool)isFinal);

    env->ReleaseFloatArrayElements(pcmArray, pcm, JNI_ABORT);
}

// --------------------------------------------------------------------------
// JNI: rb_process(handle, floatArray, frameCount, isFinal)
// Pass 2: process audio data (produces output via retrieve)
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_rb_1process(
        JNIEnv* env, jobject /*obj*/,
        jlong handle, jfloatArray pcmArray, jint frameCount, jboolean isFinal) {

    RBExportContext* ctx = (RBExportContext*)(intptr_t)handle;
    if (!ctx || !ctx->stretcher) return;

    jfloat* pcm = env->GetFloatArrayElements(pcmArray, nullptr);
    if (!pcm) return;

    int ch = ctx->channels;
    int frames = (int)frameCount;

    // De-interleave
    for (int c = 0; c < ch; c++) {
        ctx->chanBuf[c].resize(frames);
    }
    if (ch == 1) {
        memcpy(ctx->chanBuf[0].data(), pcm, frames * sizeof(float));
    } else {
        for (int i = 0; i < frames; i++) {
            ctx->chanBuf[0][i] = pcm[i * 2];
            ctx->chanBuf[1][i] = pcm[i * 2 + 1];
        }
    }

    const float* chanPtrs[2] = { ctx->chanBuf[0].data(), ctx->chanBuf[1].data() };
    ctx->stretcher->process(chanPtrs, (size_t)frames, (bool)isFinal);

    env->ReleaseFloatArrayElements(pcmArray, pcm, JNI_ABORT);

    // Drain available output → LAME → file
    drainRBAndEncode(ctx);
}

// --------------------------------------------------------------------------
// JNI: rb_flush(handle)
// Drain remaining samples + LAME flush → finalize MP3
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_rb_1flush(
        JNIEnv* /*env*/, jobject /*obj*/,
        jlong handle) {

    RBExportContext* ctx = (RBExportContext*)(intptr_t)handle;
    if (!ctx) return;

    // Drain any remaining Rubber Band output
    drainRBAndEncode(ctx);

    // Flush LAME
    int mp3BufSize = 7200;
    ctx->mp3Buf.resize(mp3BufSize);
    int encoded = lame_encode_flush(ctx->lame, ctx->mp3Buf.data(), mp3BufSize);
    if (encoded > 0) {
        fwrite(ctx->mp3Buf.data(), 1, encoded, ctx->outFile);
    }

    lame_mp3_tags_fid(ctx->lame, ctx->outFile);
    LOGI("rb_flush done");
}

// --------------------------------------------------------------------------
// JNI: rb_destroy(handle)
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_rb_1destroy(
        JNIEnv* /*env*/, jobject /*obj*/,
        jlong handle) {

    RBExportContext* ctx = (RBExportContext*)(intptr_t)handle;
    if (!ctx) return;

    if (ctx->outFile)   { fclose(ctx->outFile); ctx->outFile = nullptr; }
    if (ctx->lame)      { lame_close(ctx->lame); ctx->lame = nullptr; }
    if (ctx->stretcher) { delete ctx->stretcher; ctx->stretcher = nullptr; }
    delete ctx;
    LOGI("rb_destroy done");
}
