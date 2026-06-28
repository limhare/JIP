LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)

# Paths (absolute to avoid ndk-build path confusion)
RB_ROOT  := D:/JIP/rubberband_build/rubberband
LAME_SRC := D:/JIP/soundtouch_build/lame-3.100/libmp3lame
LAME_INC := D:/JIP/soundtouch_build/lame-3.100/include
LAME_ROOT := D:/JIP/soundtouch_build/lame-3.100

LOCAL_MODULE := rb_export

# Rubber Band: single-file compilation unit (includes all source)
# LAME: MP3 encoding source files
# JNI: our custom wrapper
LOCAL_SRC_FILES := \
    $(RB_ROOT)/single/RubberBandSingle.cpp \
    D:/JIP/rubberband_build/jni/rb_export_jni.cpp \
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

# Common include paths
LOCAL_C_INCLUDES := $(RB_ROOT) $(RB_ROOT)/src $(LAME_INC) $(LAME_ROOT) $(LAME_SRC)

# Rubber Band flags (from otherbuilds/Android.mk)
LOCAL_CPPFLAGS := -O3 -ffast-math -ftree-vectorize -fexceptions \
    -DUSE_BQRESAMPLER \
    -DUSE_BUILTIN_FFT \
    -DLACK_POSIX_MEMALIGN \
    -DUSE_OWN_ALIGNED_MALLOC \
    -DLACK_SINCOS \
    -DNO_TIMING \
    -DNO_TIMING_COMPLETE_NOOP

# LAME flags
LOCAL_CFLAGS := -O3 -ffast-math -DHAVE_CONFIG_H

LOCAL_LDLIBS := -llog -lz

include $(BUILD_SHARED_LIBRARY)
