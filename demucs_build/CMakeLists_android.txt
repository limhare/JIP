cmake_minimum_required(VERSION 3.22)
project(demucs_jni)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Release flags for Android ARM64 (no -march=native for cross-compile)
set(CMAKE_CXX_FLAGS_RELEASE "-O3 -ffast-math -DNDEBUG")

# OpenMP (Android NDK supports it via libomp)
find_package(OpenMP)

set(DEMUCS_SRC
    src/src/crosstransformer.cpp
    src/src/dsp.cpp
    src/src/encdec.cpp
    src/src/layers.cpp
    src/src/lstm.cpp
    src/src/model_apply.cpp
    src/src/model_inference.cpp
    src/src/model_load.cpp
)

add_library(demucs_jni SHARED
    ${DEMUCS_SRC}
    demucs_jni.cpp
)

target_include_directories(demucs_jni PRIVATE
    src/src
    src/vendor/eigen
)

target_link_libraries(demucs_jni log)

if(OpenMP_CXX_FOUND)
    target_link_libraries(demucs_jni OpenMP::OpenMP_CXX)
    target_compile_options(demucs_jni PRIVATE ${OpenMP_CXX_FLAGS})
endif()
