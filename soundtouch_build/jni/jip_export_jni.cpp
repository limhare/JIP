// jip_export_jni.cpp
// JNI bridge: SoundTouch + LAME MP3 export pipeline
// Java class: com.instplayer.app.AudioExporter

#include <jni.h>
#include <android/log.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

// SoundTouch
#include "SoundTouch.h"

// LAME
extern "C" {
#include "lame.h"
}

#define LOG_TAG "JIP_EXPORT"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// --------------------------------------------------------------------------
// ExportContext — holds all state for one export session
// --------------------------------------------------------------------------
struct ExportContext {
    soundtouch::SoundTouch st;
    lame_global_flags*     lame;
    FILE*                  outFile;
    int                    channels;
    int                    sampleRate;

    // SoundTouch output buffer (interleaved float)
    std::vector<float>         stOutBuf;
    // LAME MP3 output buffer
    std::vector<unsigned char> mp3Buf;

    ExportContext() : lame(nullptr), outFile(nullptr), channels(2), sampleRate(44100) {}
};

static const int ST_BLOCK = 4096;   // samples per channel passed to SoundTouch at once
static const int MP3_BUF_FACTOR = 5; // LAME recommends output buffer >= 1.25*samples + 7200

// --------------------------------------------------------------------------
// Drain SoundTouch output → LAME encode → write to file
// Returns false on encode error
// --------------------------------------------------------------------------
static bool drainAndEncode(ExportContext* ctx) {
    int ch = ctx->channels;
    while (true) {
        // Make sure stOutBuf is big enough
        uint avail = ctx->st.numSamples();
        if (avail == 0) break;

        uint toRead = (avail < (uint)ST_BLOCK) ? avail : (uint)ST_BLOCK;
        ctx->stOutBuf.resize(toRead * ch);
        uint got = ctx->st.receiveSamples(ctx->stOutBuf.data(), toRead);
        if (got == 0) break;

        // LAME encode
        // lame_encode_buffer_interleaved_ieee_float expects #samples per channel
        int mp3BufSize = (int)(got * MP3_BUF_FACTOR + 7200);
        ctx->mp3Buf.resize(mp3BufSize);

        int encoded;
        if (ch == 1) {
            // Mono: use left channel function with same pointer for both
            encoded = lame_encode_buffer_ieee_float(
                ctx->lame,
                ctx->stOutBuf.data(),   // left
                ctx->stOutBuf.data(),   // right (same = mono)
                (int)got,
                ctx->mp3Buf.data(),
                mp3BufSize);
        } else {
            encoded = lame_encode_buffer_interleaved_ieee_float(
                ctx->lame,
                ctx->stOutBuf.data(),
                (int)got,
                ctx->mp3Buf.data(),
                mp3BufSize);
        }

        if (encoded < 0) {
            LOGE("LAME encode error: %d", encoded);
            return false;
        }
        if (encoded > 0) {
            fwrite(ctx->mp3Buf.data(), 1, encoded, ctx->outFile);
        }
    }
    return true;
}

// --------------------------------------------------------------------------
// JNI: je_create(sampleRate, channels, pitchSemitones, tempoPercent, outputPath)
// Returns handle (pointer cast to jlong), or 0 on failure
// --------------------------------------------------------------------------
extern "C" JNIEXPORT jlong JNICALL
Java_com_instplayer_app_AudioExporter_je_1create(
        JNIEnv* env, jobject /*obj*/,
        jint sampleRate, jint channels,
        jfloat pitchSemitones, jfloat tempoPercent,
        jstring outputPath) {

    const char* path = env->GetStringUTFChars(outputPath, nullptr);
    LOGI("je_create: sr=%d ch=%d pitch=%.1f tempo=%.1f path=%s",
         sampleRate, channels, (float)pitchSemitones, (float)tempoPercent, path);

    ExportContext* ctx = new ExportContext();
    ctx->channels   = (int)channels;
    ctx->sampleRate = (int)sampleRate;

    // --- SoundTouch setup ---
    ctx->st.setSampleRate((uint)sampleRate);
    ctx->st.setChannels((uint)channels);
    ctx->st.setPitchSemiTones((float)pitchSemitones);
    ctx->st.setTempoChange((float)tempoPercent);   // 0 = normal, 50 = 150%
    // Quality settings for offline processing (larger than real-time defaults)
    ctx->st.setSetting(SETTING_SEQUENCE_MS, 40);
    ctx->st.setSetting(SETTING_SEEKWINDOW_MS, 15);
    ctx->st.setSetting(SETTING_OVERLAP_MS, 8);

    // --- LAME setup ---
    ctx->lame = lame_init();
    if (!ctx->lame) {
        LOGE("lame_init failed");
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }
    lame_set_num_channels(ctx->lame, channels);
    lame_set_in_samplerate(ctx->lame, sampleRate);
    lame_set_brate(ctx->lame, 320);        // 320 kbps CBR — best quality
    lame_set_quality(ctx->lame, 2);        // 2 = near-best quality, fast encode
    lame_set_mode(ctx->lame, channels == 1 ? MONO : JOINT_STEREO);
    lame_set_VBR(ctx->lame, vbr_off);

    if (lame_init_params(ctx->lame) < 0) {
        LOGE("lame_init_params failed");
        lame_close(ctx->lame);
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }

    // --- Open output file ---
    ctx->outFile = fopen(path, "wb");
    if (!ctx->outFile) {
        LOGE("Cannot open output file: %s", path);
        lame_close(ctx->lame);
        delete ctx;
        env->ReleaseStringUTFChars(outputPath, path);
        return 0;
    }

    env->ReleaseStringUTFChars(outputPath, path);
    return (jlong)(intptr_t)ctx;
}

// --------------------------------------------------------------------------
// JNI: je_feed(handle, floatArray, count)
// PCM float interleaved samples, count = number of frames (samples per channel)
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_je_1feed(
        JNIEnv* env, jobject /*obj*/,
        jlong handle, jfloatArray pcmArray, jint frameCount) {

    ExportContext* ctx = (ExportContext*)(intptr_t)handle;
    if (!ctx) return;

    jfloat* pcm = env->GetFloatArrayElements(pcmArray, nullptr);
    if (!pcm) return;

    // Feed to SoundTouch (expects interleaved float, frameCount = samples per channel)
    ctx->st.putSamples(pcm, (uint)frameCount);
    env->ReleaseFloatArrayElements(pcmArray, pcm, JNI_ABORT);

    // Drain whatever SoundTouch has ready
    drainAndEncode(ctx);
}

// --------------------------------------------------------------------------
// JNI: je_flush(handle)
// Flush SoundTouch tail + LAME flush → finalize MP3
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_je_1flush(
        JNIEnv* /*env*/, jobject /*obj*/,
        jlong handle) {

    ExportContext* ctx = (ExportContext*)(intptr_t)handle;
    if (!ctx) return;

    // Flush SoundTouch
    ctx->st.flush();
    drainAndEncode(ctx);

    // Flush LAME
    int mp3BufSize = 7200;
    ctx->mp3Buf.resize(mp3BufSize);
    int encoded = lame_encode_flush(ctx->lame, ctx->mp3Buf.data(), mp3BufSize);
    if (encoded > 0) {
        fwrite(ctx->mp3Buf.data(), 1, encoded, ctx->outFile);
    }

    // Write VBR/ID3 tag (even for CBR this writes Xing header for accurate duration)
    lame_mp3_tags_fid(ctx->lame, ctx->outFile);

    LOGI("je_flush done");
}

// --------------------------------------------------------------------------
// JNI: je_destroy(handle)
// --------------------------------------------------------------------------
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_AudioExporter_je_1destroy(
        JNIEnv* /*env*/, jobject /*obj*/,
        jlong handle) {

    ExportContext* ctx = (ExportContext*)(intptr_t)handle;
    if (!ctx) return;

    if (ctx->outFile) { fclose(ctx->outFile); ctx->outFile = nullptr; }
    if (ctx->lame)    { lame_close(ctx->lame); ctx->lame = nullptr; }
    delete ctx;
    LOGI("je_destroy done");
}
