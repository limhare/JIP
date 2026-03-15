LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)

ST_SRC   := D:/c_test/instplayer/soundtouch_build/soundtouch/source/SoundTouch
LAME_SRC := D:/c_test/instplayer/soundtouch_build/lame/libmp3lame
LAME_INC := D:/c_test/instplayer/soundtouch_build/lame/include
LAME_ROOT := D:/c_test/instplayer/soundtouch_build/lame
ST_INC   := D:/c_test/instplayer/soundtouch_build/soundtouch/include

LOCAL_MODULE    := jip_export
LOCAL_CFLAGS    := -O3 -ffast-math -DHAVE_CONFIG_H \
                   -I$(LAME_INC) -I$(LAME_ROOT) -I$(LAME_SRC)
LOCAL_CPPFLAGS  := -O3 -ffast-math -DSOUNDTOUCH_FLOAT_SAMPLES=1 -fexceptions \
                   -I$(ST_SRC) -I$(ST_INC)
LOCAL_SRC_FILES := \
    D:/c_test/instplayer/soundtouch_build/jni/cpu_detect_arm.cpp \
    D:/c_test/instplayer/soundtouch_build/jni/jip_export_jni.cpp \
    $(ST_SRC)/AAFilter.cpp \
    $(ST_SRC)/BPMDetect.cpp \
    $(ST_SRC)/FIFOSampleBuffer.cpp \
    $(ST_SRC)/FIRFilter.cpp \
    $(ST_SRC)/InterpolateCubic.cpp \
    $(ST_SRC)/InterpolateLinear.cpp \
    $(ST_SRC)/InterpolateShannon.cpp \
    $(ST_SRC)/PeakFinder.cpp \
    $(ST_SRC)/RateTransposer.cpp \
    $(ST_SRC)/SoundTouch.cpp \
    $(ST_SRC)/TDStretch.cpp \
    $(LAME_SRC)/VbrTag.c \
    $(LAME_SRC)/bitstream.c \
    $(LAME_SRC)/encoder.c \
    $(LAME_SRC)/fft.c \
    $(LAME_SRC)/gain_analysis.c \
    $(LAME_SRC)/id3tag.c \
    $(LAME_SRC)/lame.c \
    $(LAME_SRC)/mpglib_interface.c \
    $(LAME_SRC)/newmdct.c \
    $(LAME_SRC)/presets.c \
    $(LAME_SRC)/psymodel.c \
    $(LAME_SRC)/quantize.c \
    $(LAME_SRC)/quantize_pvt.c \
    $(LAME_SRC)/reservoir.c \
    $(LAME_SRC)/set_get.c \
    $(LAME_SRC)/tables.c \
    $(LAME_SRC)/takehiro.c \
    $(LAME_SRC)/util.c \
    $(LAME_SRC)/vbrquantize.c \
    $(LAME_SRC)/version.c

LOCAL_LDLIBS    := -llog -lz
include $(BUILD_SHARED_LIBRARY)
