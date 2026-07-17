#pragma once

#include <cstdint>
#include <vector>

struct GpuBlendContext;

GpuBlendContext* GpuBlendCreate(int width, int height, int blend_frames);
void GpuBlendDestroy(GpuBlendContext* ctx);
bool GpuBlendResetWindow(GpuBlendContext* ctx);
bool GpuBlendAccumulate(GpuBlendContext* ctx, const uint8_t* bgra, int stride, float weight);
bool GpuBlendPack(GpuBlendContext* ctx, std::vector<uint8_t>& out_bgra);
