// demucs_jni.cpp — Full JNI bridge for Demucs accompaniment extraction
// Input: decoded PCM float arrays (any sample rate, mono or stereo)
// Output: accompaniment.wav (drums+bass+other, 44100Hz stereo, float32 PCM)

#include <jni.h>
#include <android/log.h>
#include <string>
#include <atomic>
#include <vector>
#include <fstream>
#include <cstring>
#include <cmath>
#include <algorithm>

#define TAG "DemucsJNI"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

#include "model.hpp"
#include "tensor.hpp"

// ─── Context ─────────────────────────────────────────────────────────────────
struct DemucsContext {
    demucscpp::demucs_model model;
    bool                    model_loaded = false;
    std::atomic<float>      progress{0.0f};
    std::atomic<bool>       done{false};
    std::string             error;
    std::string             result_path;  // path to accompaniment WAV
};

// ─── Minimal 32-bit float WAV writer ─────────────────────────────────────────
static bool write_wav_f32(const std::string& path,
                          const float* left, const float* right,
                          size_t n_samples)
{
    std::ofstream f(path, std::ios::binary);
    if (!f) return false;

    const uint16_t NUM_CH   = 2;
    const uint32_t SR       = 44100;
    const uint16_t BPS      = 32;          // bits per sample
    const uint16_t FMT_FLOAT = 3;          // IEEE float
    const uint32_t BYTE_RATE = SR * NUM_CH * (BPS / 8);
    const uint16_t BLOCK_AL  = NUM_CH * (BPS / 8);
    const uint32_t DATA_SZ   = (uint32_t)(n_samples * NUM_CH * sizeof(float));
    const uint32_t CHUNK_SZ  = 36 + DATA_SZ;
    const uint32_t FMT_SZ    = 16;

    auto w2 = [&](uint16_t v){ f.write(reinterpret_cast<char*>(&v), 2); };
    auto w4 = [&](uint32_t v){ f.write(reinterpret_cast<char*>(&v), 4); };

    f.write("RIFF", 4); w4(CHUNK_SZ);
    f.write("WAVE", 4);
    f.write("fmt ", 4); w4(FMT_SZ);
    w2(FMT_FLOAT); w2(NUM_CH); w4(SR); w4(BYTE_RATE); w2(BLOCK_AL); w2(BPS);
    f.write("data", 4); w4(DATA_SZ);

    for (size_t i = 0; i < n_samples; ++i) {
        f.write(reinterpret_cast<const char*>(&left[i]),  sizeof(float));
        f.write(reinterpret_cast<const char*>(&right[i]), sizeof(float));
    }
    return f.good();
}

// ─── Linear resampler ────────────────────────────────────────────────────────
static std::vector<float> resample_linear(const float* src, size_t src_n,
                                          int src_sr, int dst_sr)
{
    if (src_sr == dst_sr) return std::vector<float>(src, src + src_n);
    size_t dst_n = (size_t)((double)src_n * dst_sr / src_sr);
    std::vector<float> dst(dst_n);
    double ratio = (double)(src_n - 1) / (double)(dst_n - 1);
    for (size_t i = 0; i < dst_n; ++i) {
        double pos = i * ratio;
        size_t p0  = (size_t)pos;
        size_t p1  = std::min(p0 + 1, src_n - 1);
        float  t   = (float)(pos - p0);
        dst[i] = src[p0] * (1.f - t) + src[p1] * t;
    }
    return dst;
}

// ─── JNI functions ───────────────────────────────────────────────────────────

// dmCreate(modelPath) → handle (0 on failure, but context still allocated)
extern "C" JNIEXPORT jlong JNICALL
Java_com_instplayer_app_DemucsProcessor_dmCreate(
    JNIEnv* env, jclass, jstring jModelPath)
{
    const char* p = env->GetStringUTFChars(jModelPath, nullptr);
    LOGI("dmCreate: loading %s", p);
    auto* ctx = new DemucsContext();
    ctx->model_loaded = demucscpp::load_demucs_model(std::string(p), &ctx->model);
    env->ReleaseStringUTFChars(jModelPath, p);
    if (!ctx->model_loaded) {
        LOGE("dmCreate: model load failed");
        ctx->error = "Model load failed";
    } else {
        LOGI("dmCreate: OK, is_4sources=%d", ctx->model.is_4sources);
    }
    return reinterpret_cast<jlong>(ctx);
}

// dmSeparatePCM(handle, leftPCM[], rightPCM[], sampleRate, outputDir)
// Blocking — call from a background Java thread.
// leftPCM/rightPCM: float samples in [-1, 1].  If null or length 0: error.
// sampleRate: original sample rate (will resample to 44100 if needed).
extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_DemucsProcessor_dmSeparatePCM(
    JNIEnv* env, jclass,
    jlong       handle,
    jfloatArray jLeft,
    jfloatArray jRight,
    jint        sampleRate,
    jstring     jOutputDir)
{
    auto* ctx = reinterpret_cast<DemucsContext*>(handle);
    if (!ctx || !ctx->model_loaded) {
        LOGE("dmSeparatePCM: invalid context"); return;
    }
    ctx->done     = false;
    ctx->progress = 0.0f;
    ctx->error.clear();
    ctx->result_path.clear();

    jsize n = (jLeft != nullptr) ? env->GetArrayLength(jLeft) : 0;
    if (n == 0) {
        ctx->error = "Empty PCM input"; ctx->done = true; return;
    }

    const char* od = env->GetStringUTFChars(jOutputDir, nullptr);
    std::string outDir(od);
    env->ReleaseStringUTFChars(jOutputDir, od);

    // Copy PCM arrays
    jfloat* lPtr = env->GetFloatArrayElements(jLeft,  nullptr);
    jfloat* rPtr = env->GetFloatArrayElements(jRight, nullptr);

    const int TARGET_SR = demucscpp::SUPPORTED_SAMPLE_RATE;  // 44100

    std::vector<float> lv, rv;
    if ((int)sampleRate != TARGET_SR) {
        LOGI("dmSeparatePCM: resampling %d → %d", (int)sampleRate, TARGET_SR);
        lv = resample_linear(lPtr, n, sampleRate, TARGET_SR);
        rv = resample_linear(rPtr, n, sampleRate, TARGET_SR);
    } else {
        lv.assign(lPtr, lPtr + n);
        rv.assign(rPtr, rPtr + n);
    }

    env->ReleaseFloatArrayElements(jLeft,  lPtr, JNI_ABORT);
    env->ReleaseFloatArrayElements(jRight, rPtr, JNI_ABORT);

    size_t N = lv.size();
    LOGI("dmSeparatePCM: %zu samples @ 44100Hz, out=%s", N, outDir.c_str());

    // Build Eigen matrix (2 x N)
    Eigen::MatrixXf audio(2, (Eigen::Index)N);
    for (size_t i = 0; i < N; ++i) {
        audio(0, (Eigen::Index)i) = lv[i];
        audio(1, (Eigen::Index)i) = rv[i];
    }
    lv.clear(); lv.shrink_to_fit();
    rv.clear(); rv.shrink_to_fit();

    // Progress callback
    demucscpp::ProgressCallback cb = [ctx](float p, const std::string& msg) {
        ctx->progress = p;
        LOGI("Progress %.1f%% %s", p * 100.f, msg.c_str());
    };

    // Run Demucs inference (may take 1-5 min)
    Eigen::Tensor3dXf out = demucscpp::demucs_inference(ctx->model, audio, cb);

    int n_src  = ctx->model.is_4sources ? 4 : 6;
    int n_samp = (int)out.dimension(2);

    // Sum drums(0)+bass(1)+other(2) → accompaniment  (skip vocals=3)
    std::vector<float> acc_l(n_samp, 0.f), acc_r(n_samp, 0.f);
    int acc_count = std::min(3, n_src);
    for (int s = 0; s < acc_count; ++s) {
        for (int i = 0; i < n_samp; ++i) {
            acc_l[i] += out(s, 0, i);
            acc_r[i] += out(s, 1, i);
        }
    }

    // Normalise if clipping (simple peak norm)
    float peak = 0.f;
    for (int i = 0; i < n_samp; ++i) {
        peak = std::max(peak, std::abs(acc_l[i]));
        peak = std::max(peak, std::abs(acc_r[i]));
    }
    if (peak > 0.99f) {
        float scale = 0.99f / peak;
        for (int i = 0; i < n_samp; ++i) { acc_l[i] *= scale; acc_r[i] *= scale; }
    }

    std::string wavPath = outDir + "/accompaniment.wav";
    if (!write_wav_f32(wavPath, acc_l.data(), acc_r.data(), (size_t)n_samp)) {
        ctx->error = "Failed to write " + wavPath;
        LOGE("%s", ctx->error.c_str());
    } else {
        ctx->result_path = wavPath;
        LOGI("dmSeparatePCM: wrote %s", wavPath.c_str());
    }

    ctx->progress = 1.0f;
    ctx->done     = true;
}

extern "C" JNIEXPORT jint JNICALL
Java_com_instplayer_app_DemucsProcessor_dmGetProgress(JNIEnv*, jclass, jlong handle)
{
    auto* ctx = reinterpret_cast<DemucsContext*>(handle);
    if (!ctx) return 0;
    return static_cast<jint>(ctx->progress.load() * 100.f);
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_instplayer_app_DemucsProcessor_dmIsDone(JNIEnv*, jclass, jlong handle)
{
    auto* ctx = reinterpret_cast<DemucsContext*>(handle);
    if (!ctx) return JNI_TRUE;
    return ctx->done.load() ? JNI_TRUE : JNI_FALSE;
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_instplayer_app_DemucsProcessor_dmGetError(JNIEnv* env, jclass, jlong handle)
{
    auto* ctx = reinterpret_cast<DemucsContext*>(handle);
    if (!ctx) return env->NewStringUTF("null context");
    return env->NewStringUTF(ctx->error.c_str());
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_instplayer_app_DemucsProcessor_dmGetResult(JNIEnv* env, jclass, jlong handle)
{
    auto* ctx = reinterpret_cast<DemucsContext*>(handle);
    if (!ctx) return env->NewStringUTF("");
    return env->NewStringUTF(ctx->result_path.c_str());
}

extern "C" JNIEXPORT void JNICALL
Java_com_instplayer_app_DemucsProcessor_dmDestroy(JNIEnv*, jclass, jlong handle)
{
    delete reinterpret_cast<DemucsContext*>(handle);
    LOGI("dmDestroy: done");
}
