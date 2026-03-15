// ARM stub for CPU extension detection
// ARM NEON is handled separately by SoundTouch internals
#include "cpu_detect.h"

uint detectCPUextensions(void)
{
    return 0; // No x86 SIMD on ARM; SoundTouch uses portable code path
}
