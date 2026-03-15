#include <jni.h>
#include "SoundTouchDLL.h"

// JNI bridge: Java com.instplayer.app.SoundTouchProcessor <-> SoundTouch C API
// Method naming: Java_<pkg>_<class>_<method>  (underscores in pkg/class escaped as _1)

#define JNI_METHOD(name) \
    Java_com_instplayer_app_SoundTouchProcessor_##name

extern "C" {

JNIEXPORT jlong JNICALL JNI_METHOD(st_1createInstance)(JNIEnv*, jclass) {
    return (jlong)soundtouch_createInstance();
}

JNIEXPORT void JNICALL JNI_METHOD(st_1destroyInstance)(JNIEnv*, jclass, jlong h) {
    soundtouch_destroyInstance((HANDLE)h);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1setSampleRate)(JNIEnv*, jclass, jlong h, jint rate) {
    soundtouch_setSampleRate((HANDLE)h, (unsigned int)rate);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1setChannels)(JNIEnv*, jclass, jlong h, jint ch) {
    soundtouch_setChannels((HANDLE)h, (unsigned int)ch);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1setPitchSemiTones)(JNIEnv*, jclass, jlong h, jfloat s) {
    soundtouch_setPitchSemiTones((HANDLE)h, s);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1setTempoChange)(JNIEnv*, jclass, jlong h, jfloat t) {
    soundtouch_setTempoChange((HANDLE)h, t);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1putSamples)(JNIEnv* env, jclass,
        jlong h, jfloatArray samples, jint numSamples) {
    jfloat* buf = env->GetFloatArrayElements(samples, nullptr);
    soundtouch_putSamples((HANDLE)h, buf, (unsigned int)numSamples);
    env->ReleaseFloatArrayElements(samples, buf, JNI_ABORT);
}

JNIEXPORT jint JNICALL JNI_METHOD(st_1receiveSamples)(JNIEnv* env, jclass,
        jlong h, jfloatArray outBuf, jint maxSamples) {
    jfloat* buf = env->GetFloatArrayElements(outBuf, nullptr);
    unsigned int received = soundtouch_receiveSamples((HANDLE)h, buf, (unsigned int)maxSamples);
    env->ReleaseFloatArrayElements(outBuf, buf, 0); // 0 = copy back
    return (jint)received;
}

JNIEXPORT jint JNICALL JNI_METHOD(st_1numSamples)(JNIEnv*, jclass, jlong h) {
    return (jint)soundtouch_numSamples((HANDLE)h);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1flush)(JNIEnv*, jclass, jlong h) {
    soundtouch_flush((HANDLE)h);
}

JNIEXPORT void JNICALL JNI_METHOD(st_1clear)(JNIEnv*, jclass, jlong h) {
    soundtouch_clear((HANDLE)h);
}

} // extern "C"
