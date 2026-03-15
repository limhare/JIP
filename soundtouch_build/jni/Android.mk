LOCAL_PATH := $(call my-dir)

ST_SRC := D:/c_test/instplayer/soundtouch_build/soundtouch/source

include $(CLEAR_VARS)

LOCAL_MODULE    := soundtouch4c
LOCAL_CPPFLAGS  := -O3 -ffast-math -DSOUNDTOUCH_FLOAT_SAMPLES=1 -fexceptions

LOCAL_C_INCLUDES := \
    $(ST_SRC)/SoundTouch \
    $(ST_SRC)/SoundTouchDLL \
    D:/c_test/instplayer/soundtouch_build/soundtouch/include

LOCAL_SRC_FILES := \
    D:/c_test/instplayer/soundtouch_build/jni/cpu_detect_arm.cpp \
    D:/c_test/instplayer/soundtouch_build/jni/soundtouch_jni.cpp \
    $(ST_SRC)/SoundTouch/AAFilter.cpp \
    $(ST_SRC)/SoundTouch/BPMDetect.cpp \
    $(ST_SRC)/SoundTouch/FIFOSampleBuffer.cpp \
    $(ST_SRC)/SoundTouch/FIRFilter.cpp \
    $(ST_SRC)/SoundTouch/InterpolateCubic.cpp \
    $(ST_SRC)/SoundTouch/InterpolateLinear.cpp \
    $(ST_SRC)/SoundTouch/InterpolateShannon.cpp \
    $(ST_SRC)/SoundTouch/PeakFinder.cpp \
    $(ST_SRC)/SoundTouch/RateTransposer.cpp \
    $(ST_SRC)/SoundTouch/SoundTouch.cpp \
    $(ST_SRC)/SoundTouch/TDStretch.cpp \
    $(ST_SRC)/SoundTouchDLL/SoundTouchDLL.cpp

LOCAL_LDLIBS := -llog

include $(BUILD_SHARED_LIBRARY)
