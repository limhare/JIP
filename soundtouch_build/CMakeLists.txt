cmake_minimum_required(VERSION 3.18)
project(soundtouch4c)

set(ST_SRC ${CMAKE_CURRENT_SOURCE_DIR}/soundtouch/source)

set(SOUNDTOUCH_SRCS
    ${ST_SRC}/SoundTouch/AAFilter.cpp
    ${ST_SRC}/SoundTouch/FIFOSampleBuffer.cpp
    ${ST_SRC}/SoundTouch/FIRFilter.cpp
    ${ST_SRC}/SoundTouch/InterpolateCubic.cpp
    ${ST_SRC}/SoundTouch/InterpolateLinear.cpp
    ${ST_SRC}/SoundTouch/InterpolateShannon.cpp
    ${ST_SRC}/SoundTouch/PeakFinder.cpp
    ${ST_SRC}/SoundTouch/RateTransposer.cpp
    ${ST_SRC}/SoundTouch/SoundTouch.cpp
    ${ST_SRC}/SoundTouch/TDStretch.cpp
    ${ST_SRC}/SoundTouchDLL/SoundTouchDLL.cpp
)

add_library(soundtouch4c SHARED ${SOUNDTOUCH_SRCS})

target_include_directories(soundtouch4c PRIVATE
    ${ST_SRC}/SoundTouch
    ${ST_SRC}/SoundTouchDLL
    ${ST_SRC}/include
)

target_compile_definitions(soundtouch4c PRIVATE
    SOUNDTOUCH_FLOAT_SAMPLES=1
    ST_NO_EXCEPTION_HANDLING=1
    ANDROID=1
)

target_compile_options(soundtouch4c PRIVATE -O3 -ffast-math)
