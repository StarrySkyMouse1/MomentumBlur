#pragma once

#include "mmod_native.h"

#include <cstdint>
#include <vector>

struct GpuBlendContext;

GpuBlendContext* GpuBlendCreate(int width, int height, int blend_frames);
void GpuBlendDestroy(GpuBlendContext* ctx);
bool GpuBlendResetWindow(GpuBlendContext* ctx);
bool GpuBlendAccumulate(GpuBlendContext* ctx, const uint8_t* bgra, int stride, float weight);
bool GpuBlendPack(GpuBlendContext* ctx, std::vector<uint8_t>& out_bgra);

/*
 * Quality processing (GPU path). Called once per output frame after the blend
 * window is accumulated and before packing. With no enabled effects this is a
 * no-op and the old fast path is preserved.
 */
bool GpuBlendConfigureEffects(GpuBlendContext* ctx,
                              const MmodEffectDescV1* effects,
                              int32_t count,
                              char* error_message,
                              size_t error_message_size);
bool GpuBlendProcess(GpuBlendContext* ctx);
bool GpuBlendHasEnabledEffects(GpuBlendContext* ctx);
