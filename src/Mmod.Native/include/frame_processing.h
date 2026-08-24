#pragma once

#include "mmod_native.h"

#include <cstdint>
#include <vector>

/*
 * CPU reference/fallback implementation of the V1 quality effects.
 *
 * The GPU path lives in gpu_blend.cpp; the math here mirrors those shaders so
 * the pipeline behaves identically (correctly, if slowly) when D3D11 is
 * unavailable. Effects run in fixed logical stages:
 *   1. Temporal (e.g. Temporal Shimmer Reduction)
 *   2. Motion-aware spatial (Motion-Adaptive Detail Reduction)
 *   3. Global spatial (Micro Detail Low-Pass, Deband)
 * within a stage, the descriptor `order` field sorts the effects.
 */

struct FrameProcessingState;

FrameProcessingState* FrameProcessingCreate(int width, int height);
void FrameProcessingDestroy(FrameProcessingState* state);

/* Applies the enabled effects to `rgb` in place (float RGB 0..255).
   Unknown effect types are ignored. Returns false only on invalid arguments. */
bool FrameProcessingApply(FrameProcessingState* state,
                          float* rgb,
                          const MmodEffectDescV1* effects,
                          int32_t effect_count);
