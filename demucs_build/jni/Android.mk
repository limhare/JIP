LOCAL_PATH := $(call my-dir)

DEMUCS_SRC := D:/c_test/instplayer/demucs_build/src/src
EIGEN_INC  := D:/c_test/instplayer/demucs_build/src/vendor/eigen

include $(CLEAR_VARS)

LOCAL_MODULE    := demucs_jni
LOCAL_CPPFLAGS  := -O3 -ffast-math -DNDEBUG -fexceptions -frtti \
                   -std=c++17 \
                   -fopenmp \
                   -I$(DEMUCS_SRC) \
                   -I$(EIGEN_INC)

LOCAL_SRC_FILES := \
    D:/c_test/instplayer/demucs_build/demucs_jni.cpp \
    $(DEMUCS_SRC)/crosstransformer.cpp \
    $(DEMUCS_SRC)/dsp.cpp \
    $(DEMUCS_SRC)/encdec.cpp \
    $(DEMUCS_SRC)/layers.cpp \
    $(DEMUCS_SRC)/lstm.cpp \
    $(DEMUCS_SRC)/model_apply.cpp \
    $(DEMUCS_SRC)/model_inference.cpp \
    $(DEMUCS_SRC)/model_load.cpp

LOCAL_LDLIBS    := -llog -lm
LOCAL_LDFLAGS   := -fopenmp -static-openmp

include $(BUILD_SHARED_LIBRARY)
