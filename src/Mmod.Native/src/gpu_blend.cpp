#include "gpu_blend.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;

namespace {

static const char* kClearCs = R"(
RWTexture2D<float4> AccTex : register(u0);
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; AccTex.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  AccTex[id.xy] = float4(0,0,0,0);
}
)";

static const char* kPackCs = R"(
RWTexture2D<float4> AccTex : register(u0);
RWTexture2D<uint> OutTex : register(u1);
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; AccTex.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  float3 rgb = saturate(AccTex[id.xy].rgb);
  uint r = (uint)(rgb.r * 255.0f + 0.5f);
  uint g = (uint)(rgb.g * 255.0f + 0.5f);
  uint b = (uint)(rgb.b * 255.0f + 0.5f);
  OutTex[id.xy] = b | (g << 8) | (r << 16) | (255u << 24);
}
)";

static const char* kAccumulateCsV2 = R"(
Texture2D<uint> InputTex : register(t0);
RWTexture2D<float4> AccTex : register(u0);
cbuffer Params : register(b0) { float Weight; float3 Pad; };
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; AccTex.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  uint p = InputTex.Load(int3(id.xy, 0));
  float b = (float)(p & 255) / 255.0f;
  float g = (float)((p >> 8) & 255) / 255.0f;
  float r = (float)((p >> 16) & 255) / 255.0f;
  float4 a = AccTex[id.xy];
  AccTex[id.xy] = float4(a.rgb + float3(r, g, b) * Weight, 1.0);
}
)";

/* All processing shaders operate on float RGB in 0..255 space, matching the
   CPU fallback in frame_processing.cpp. p0..p3 map to the descriptor p0..p3. */

static const char* kMotionAdaptiveCs = R"(
Texture2D<float4> Cur : register(t0);
Texture2D<float4> Prev : register(t1);
RWTexture2D<float4> Out : register(u0);
cbuffer Params : register(b0) { float4 P; }; // x=strength y=motion-threshold z=edge-protection

float3 LoadC(Texture2D<float4> tex, int2 p, int w, int h) {
  int2 c = clamp(p, int2(0, 0), int2(w - 1, h - 1));
  return tex.Load(int3(c, 0)).rgb;
}
float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; Cur.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  int2 p = int2((int)id.x, (int)id.y);
  float3 cur = Cur.Load(int3(p, 0)).rgb;
  float3 prev = Prev.Load(int3(p, 0)).rgb;

  float motion = abs(Luma(cur) - Luma(prev)) / 255.0;
  float th = max(P.y, 0.001);
  float mask = smoothstep(th, th * 2.5, motion);

  float3 blur = 0;
  for (int dy = -1; dy <= 1; ++dy)
    for (int dx = -1; dx <= 1; ++dx)
      blur += LoadC(Cur, p + int2(dx, dy), w, h);
  blur /= 9.0;

  float gx = abs(Luma(LoadC(Cur, p + int2(1, 0), w, h)) - Luma(LoadC(Cur, p + int2(-1, 0), w, h)));
  float gy = abs(Luma(LoadC(Cur, p + int2(0, 1), w, h)) - Luma(LoadC(Cur, p + int2(0, -1), w, h)));
  float edge = sqrt(gx * gx + gy * gy) / 255.0;
  float edge_mask = saturate(edge / 0.6);
  float protect = 1.0 - edge_mask * P.z;

  float t = mask * P.x * protect;
  Out[id.xy] = float4(lerp(cur, blur, t), 1.0);
}
)";

static const char* kLowPassCs = R"(
Texture2D<float4> Cur : register(t0);
RWTexture2D<float4> Out : register(u0);
cbuffer Params : register(b0) { float4 P; }; // x=strength y=radius

float3 LoadC(Texture2D<float4> tex, int2 p, int w, int h) {
  int2 c = clamp(p, int2(0, 0), int2(w - 1, h - 1));
  return tex.Load(int3(c, 0)).rgb;
}

[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; Cur.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  int2 p = int2((int)id.x, (int)id.y);
  int radius = (P.y >= 1.5) ? 2 : 1;
  float3 acc = 0;
  for (int dy = -radius; dy <= radius; ++dy)
    for (int dx = -radius; dx <= radius; ++dx)
      acc += LoadC(Cur, p + int2(dx, dy), w, h);
  float3 blur = acc / (float)((2 * radius + 1) * (2 * radius + 1));
  float3 cur = Cur.Load(int3(p, 0)).rgb;
  Out[id.xy] = float4(lerp(cur, blur, P.x), 1.0);
}
)";

static const char* kDebandCs = R"(
Texture2D<float4> Cur : register(t0);
RWTexture2D<float4> Out : register(u0);
cbuffer Params : register(b0) { float4 P; }; // x=strength y=threshold

float3 LoadC(Texture2D<float4> tex, int2 p, int w, int h) {
  int2 c = clamp(p, int2(0, 0), int2(w - 1, h - 1));
  return tex.Load(int3(c, 0)).rgb;
}

[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; Cur.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  int2 p = int2((int)id.x, (int)id.y);
  float3 acc = 0;
  for (int dy = -2; dy <= 2; ++dy)
    for (int dx = -2; dx <= 2; ++dx)
      acc += LoadC(Cur, p + int2(dx, dy), w, h);
  float3 mean = acc / 25.0;
  float3 cur = Cur.Load(int3(p, 0)).rgb;
  float diff = max(max(abs(cur.r - mean.r), abs(cur.g - mean.g)), abs(cur.b - mean.b)) / 255.0;
  float th = max(P.y, 0.001);
  float flat = 1.0 - smoothstep(th, th * 3.0, diff);
  Out[id.xy] = float4(lerp(cur, mean, P.x * flat), 1.0);
}
)";

static const char* kShimmerCs = R"(
Texture2D<float4> Cur : register(t0);
Texture2D<float4> Prev : register(t1);
RWTexture2D<float4> Out : register(u0);
cbuffer Params : register(b0) { float4 P; }; // x=strength y=temporal-threshold

float3 LoadC(Texture2D<float4> tex, int2 p, int w, int h) {
  int2 c = clamp(p, int2(0, 0), int2(w - 1, h - 1));
  return tex.Load(int3(c, 0)).rgb;
}
float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  uint w, h; Cur.GetDimensions(w, h);
  if (id.x >= w || id.y >= h) return;
  int2 p = int2((int)id.x, (int)id.y);
  float3 cur = Cur.Load(int3(p, 0)).rgb;
  float3 prev = Prev.Load(int3(p, 0)).rgb;

  float diff = max(max(abs(cur.r - prev.r), abs(cur.g - prev.g)), abs(cur.b - prev.b)) / 255.0;
  float th = max(P.y, 0.001);
  float k = 1.0 - smoothstep(th * 0.5, th * 2.0, diff);

  float3 acc = 0;
  for (int dy = -1; dy <= 1; ++dy)
    for (int dx = -1; dx <= 1; ++dx)
      acc += LoadC(Cur, p + int2(dx, dy), w, h);
  float3 blur = acc / 9.0;
  float hf = abs(Luma(cur) - Luma(blur)) / 255.0;
  float hf_mask = smoothstep(0.02, 0.10, hf);

  Out[id.xy] = float4(lerp(cur, prev, P.x * k * hf_mask), 1.0);
}
)";

HRESULT CompileCs(const char* src, const char* entry, ID3DBlob** blob) {
  ComPtr<ID3DBlob> error;
  HRESULT hr = D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, entry, "cs_5_0",
                          D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, blob, &error);
  return hr;
}

int EffectStage(int32_t effect_type) {
  switch (effect_type) {
    case MmodEffect_TemporalShimmerReduction: return 0;
    case MmodEffect_MotionAdaptiveDetail: return 1;
    case MmodEffect_MicroDetailLowPass:
    case MmodEffect_DebandNoDither: return 2;
    default: return 3;
  }
}

} // namespace

struct GpuBlendContext {
  int width = 0;
  int height = 0;
  ComPtr<ID3D11Device> device;
  ComPtr<ID3D11DeviceContext> context;
  ComPtr<ID3D11Texture2D> input_tex;
  ComPtr<ID3D11Texture2D> input_staging;
  ComPtr<ID3D11ShaderResourceView> input_srv;
  ComPtr<ID3D11Texture2D> acc_tex;
  ComPtr<ID3D11UnorderedAccessView> acc_uav;
  ComPtr<ID3D11Texture2D> out_tex;
  ComPtr<ID3D11UnorderedAccessView> out_uav;
  ComPtr<ID3D11Texture2D> out_staging;
  ComPtr<ID3D11Buffer> weight_cb;
  ComPtr<ID3D11ComputeShader> cs_clear;
  ComPtr<ID3D11ComputeShader> cs_accum;
  ComPtr<ID3D11ComputeShader> cs_pack;

  // Quality processing resources (created lazily in GpuBlendConfigureEffects).
  std::vector<MmodEffectDescV1> enabled_effects;
  bool first_processed = true;
  bool processing_configured = false;
  ComPtr<ID3D11Texture2D> work_a_tex;
  ComPtr<ID3D11Texture2D> work_b_tex;
  ComPtr<ID3D11ShaderResourceView> work_a_srv;
  ComPtr<ID3D11ShaderResourceView> work_b_srv;
  ComPtr<ID3D11UnorderedAccessView> work_a_uav;
  ComPtr<ID3D11UnorderedAccessView> work_b_uav;
  ComPtr<ID3D11Texture2D> prev_prequality_tex;
  ComPtr<ID3D11ShaderResourceView> prev_prequality_srv;
  ComPtr<ID3D11Texture2D> prev_preprocessed_tex;
  ComPtr<ID3D11ShaderResourceView> prev_preprocessed_srv;
  ComPtr<ID3D11Buffer> effect_cb;
  ComPtr<ID3D11ComputeShader> cs_motion_adaptive;
  ComPtr<ID3D11ComputeShader> cs_lowpass;
  ComPtr<ID3D11ComputeShader> cs_deband;
  ComPtr<ID3D11ComputeShader> cs_shimmer;
};

static bool CreateFloatTexture(GpuBlendContext* ctx,
                               ID3D11Texture2D** tex,
                               ID3D11ShaderResourceView** srv,
                               ID3D11UnorderedAccessView** uav) {
  D3D11_TEXTURE2D_DESC td{};
  td.Width = static_cast<UINT>(ctx->width);
  td.Height = static_cast<UINT>(ctx->height);
  td.MipLevels = 1;
  td.ArraySize = 1;
  td.SampleDesc.Count = 1;
  td.Usage = D3D11_USAGE_DEFAULT;
  td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
  td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
  if (FAILED(ctx->device->CreateTexture2D(&td, nullptr, tex))) return false;
  if (srv && FAILED(ctx->device->CreateShaderResourceView(*tex, nullptr, srv))) return false;
  if (uav && FAILED(ctx->device->CreateUnorderedAccessView(*tex, nullptr, uav))) return false;
  return true;
}

GpuBlendContext* GpuBlendCreate(int width, int height, int /*blend_frames*/) {
  if (width <= 0 || height <= 0) return nullptr;

  auto* ctx = new GpuBlendContext();
  ctx->width = width;
  ctx->height = height;

  D3D_FEATURE_LEVEL level;
  UINT flags = D3D11_CREATE_DEVICE_SINGLETHREADED;
#if defined(_DEBUG)
  flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
  HRESULT hr = D3D11CreateDevice(
      nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
      D3D11_SDK_VERSION, &ctx->device, &level, &ctx->context);
  if (FAILED(hr)) {
    delete ctx;
    return nullptr;
  }

  ComPtr<ID3DBlob> clear_blob, accum_blob, pack_blob;
  if (FAILED(CompileCs(kClearCs, "main", &clear_blob)) ||
      FAILED(CompileCs(kAccumulateCsV2, "main", &accum_blob)) ||
      FAILED(CompileCs(kPackCs, "main", &pack_blob))) {
    delete ctx;
    return nullptr;
  }
  ctx->device->CreateComputeShader(clear_blob->GetBufferPointer(), clear_blob->GetBufferSize(), nullptr, &ctx->cs_clear);
  ctx->device->CreateComputeShader(accum_blob->GetBufferPointer(), accum_blob->GetBufferSize(), nullptr, &ctx->cs_accum);
  ctx->device->CreateComputeShader(pack_blob->GetBufferPointer(), pack_blob->GetBufferSize(), nullptr, &ctx->cs_pack);

  D3D11_TEXTURE2D_DESC td{};
  td.Width = width;
  td.Height = height;
  td.MipLevels = 1;
  td.ArraySize = 1;
  td.SampleDesc.Count = 1;
  td.Usage = D3D11_USAGE_DEFAULT;

  // Input as R32_UINT packing BGRA
  td.Format = DXGI_FORMAT_R32_UINT;
  td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
  ctx->device->CreateTexture2D(&td, nullptr, &ctx->input_tex);
  ctx->device->CreateShaderResourceView(ctx->input_tex.Get(), nullptr, &ctx->input_srv);

  td.Usage = D3D11_USAGE_STAGING;
  td.BindFlags = 0;
  td.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
  ctx->device->CreateTexture2D(&td, nullptr, &ctx->input_staging);

  // Accumulator float4
  td.Usage = D3D11_USAGE_DEFAULT;
  td.CPUAccessFlags = 0;
  td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
  td.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
  ctx->device->CreateTexture2D(&td, nullptr, &ctx->acc_tex);
  ctx->device->CreateUnorderedAccessView(ctx->acc_tex.Get(), nullptr, &ctx->acc_uav);

  // Output packed uint
  td.Format = DXGI_FORMAT_R32_UINT;
  td.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
  ctx->device->CreateTexture2D(&td, nullptr, &ctx->out_tex);
  ctx->device->CreateUnorderedAccessView(ctx->out_tex.Get(), nullptr, &ctx->out_uav);

  td.Usage = D3D11_USAGE_STAGING;
  td.BindFlags = 0;
  td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
  ctx->device->CreateTexture2D(&td, nullptr, &ctx->out_staging);

  D3D11_BUFFER_DESC cbd{};
  cbd.ByteWidth = 16;
  cbd.Usage = D3D11_USAGE_DYNAMIC;
  cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
  cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
  ctx->device->CreateBuffer(&cbd, nullptr, &ctx->weight_cb);

  if (!ctx->input_tex || !ctx->acc_tex || !ctx->out_tex || !ctx->cs_accum) {
    delete ctx;
    return nullptr;
  }
  return ctx;
}

void GpuBlendDestroy(GpuBlendContext* ctx) {
  delete ctx;
}

bool GpuBlendResetWindow(GpuBlendContext* ctx) {
  if (!ctx) return false;
  ctx->context->CSSetShader(ctx->cs_clear.Get(), nullptr, 0);
  ID3D11UnorderedAccessView* uavs[] = { ctx->acc_uav.Get() };
  ctx->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
  const UINT gx = (ctx->width + 15) / 16;
  const UINT gy = (ctx->height + 15) / 16;
  ctx->context->Dispatch(gx, gy, 1);
  ID3D11UnorderedAccessView* nulluav[] = { nullptr };
  ctx->context->CSSetUnorderedAccessViews(0, 1, nulluav, nullptr);
  return true;
}

bool GpuBlendAccumulate(GpuBlendContext* ctx, const uint8_t* bgra, int stride, float weight) {
  if (!ctx || !bgra) return false;

  D3D11_MAPPED_SUBRESOURCE mapped{};
  if (FAILED(ctx->context->Map(ctx->input_staging.Get(), 0, D3D11_MAP_WRITE, 0, &mapped)))
    return false;

  for (int y = 0; y < ctx->height; ++y) {
    const uint8_t* src = bgra + static_cast<size_t>(y) * static_cast<size_t>(stride);
    auto* dst = reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch);
    for (int x = 0; x < ctx->width; ++x) {
      const int i = x * 4;
      dst[x] = static_cast<uint32_t>(src[i]) |
               (static_cast<uint32_t>(src[i + 1]) << 8) |
               (static_cast<uint32_t>(src[i + 2]) << 16) |
               (static_cast<uint32_t>(src[i + 3]) << 24);
    }
  }
  ctx->context->Unmap(ctx->input_staging.Get(), 0);
  ctx->context->CopyResource(ctx->input_tex.Get(), ctx->input_staging.Get());

  D3D11_MAPPED_SUBRESOURCE cbmap{};
  if (SUCCEEDED(ctx->context->Map(ctx->weight_cb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &cbmap))) {
    float* f = static_cast<float*>(cbmap.pData);
    f[0] = weight;
    f[1] = f[2] = f[3] = 0.f;
    ctx->context->Unmap(ctx->weight_cb.Get(), 0);
  }

  ctx->context->CSSetShader(ctx->cs_accum.Get(), nullptr, 0);
  ID3D11ShaderResourceView* srvs[] = { ctx->input_srv.Get() };
  ctx->context->CSSetShaderResources(0, 1, srvs);
  ID3D11UnorderedAccessView* uavs[] = { ctx->acc_uav.Get() };
  ctx->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
  ID3D11Buffer* cbs[] = { ctx->weight_cb.Get() };
  ctx->context->CSSetConstantBuffers(0, 1, cbs);
  ctx->context->Dispatch((ctx->width + 15) / 16, (ctx->height + 15) / 16, 1);

  ID3D11ShaderResourceView* nullsrv[] = { nullptr };
  ID3D11UnorderedAccessView* nulluav[] = { nullptr };
  ctx->context->CSSetShaderResources(0, 1, nullsrv);
  ctx->context->CSSetUnorderedAccessViews(0, 1, nulluav, nullptr);
  return true;
}

bool GpuBlendPack(GpuBlendContext* ctx, std::vector<uint8_t>& out_bgra) {
  if (!ctx) return false;
  ctx->context->CSSetShader(ctx->cs_pack.Get(), nullptr, 0);
  ID3D11UnorderedAccessView* uavs[] = { ctx->acc_uav.Get(), ctx->out_uav.Get() };
  ctx->context->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
  ctx->context->Dispatch((ctx->width + 15) / 16, (ctx->height + 15) / 16, 1);
  ID3D11UnorderedAccessView* nulluav[] = { nullptr, nullptr };
  ctx->context->CSSetUnorderedAccessViews(0, 2, nulluav, nullptr);

  ctx->context->CopyResource(ctx->out_staging.Get(), ctx->out_tex.Get());
  D3D11_MAPPED_SUBRESOURCE mapped{};
  if (FAILED(ctx->context->Map(ctx->out_staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
    return false;

  out_bgra.resize(static_cast<size_t>(ctx->width) * ctx->height * 4u);
  for (int y = 0; y < ctx->height; ++y) {
    const auto* src = reinterpret_cast<const uint32_t*>(
        static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch);
    uint8_t* dst = out_bgra.data() + static_cast<size_t>(y) * ctx->width * 4u;
    for (int x = 0; x < ctx->width; ++x) {
      const uint32_t p = src[x];
      dst[x * 4 + 0] = static_cast<uint8_t>(p & 255);
      dst[x * 4 + 1] = static_cast<uint8_t>((p >> 8) & 255);
      dst[x * 4 + 2] = static_cast<uint8_t>((p >> 16) & 255);
      dst[x * 4 + 3] = static_cast<uint8_t>((p >> 24) & 255);
    }
  }
  ctx->context->Unmap(ctx->out_staging.Get(), 0);
  return true;
}

bool GpuBlendConfigureEffects(GpuBlendContext* ctx,
                              const MmodEffectDescV1* effects,
                              int32_t count,
                              char* error_message,
                              size_t error_message_size) {
  if (!ctx) return false;
  if (error_message && error_message_size > 0) error_message[0] = '\0';

  ctx->enabled_effects.clear();
  if (!effects || count <= 0) {
    ctx->processing_configured = true;
    return true;
  }
  for (int32_t i = 0; i < count; ++i) {
    if (effects[i].enabled && effects[i].effect_type != MmodEffect_None) {
      ctx->enabled_effects.push_back(effects[i]);
    }
  }
  if (ctx->enabled_effects.empty()) {
    ctx->processing_configured = true;
    return true;
  }

  std::stable_sort(ctx->enabled_effects.begin(), ctx->enabled_effects.end(),
                   [](const MmodEffectDescV1& a, const MmodEffectDescV1& b) {
                     const int sa = EffectStage(a.effect_type);
                     const int sb = EffectStage(b.effect_type);
                     if (sa != sb) return sa < sb;
                     return a.order < b.order;
                   });

  ComPtr<ID3DBlob> blob;
  if (FAILED(CompileCs(kMotionAdaptiveCs, "main", &blob))) {
    if (error_message) snprintf(error_message, error_message_size, "compile motion-adaptive-detail shader failed");
    return false;
  }
  ctx->device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &ctx->cs_motion_adaptive);
  blob.Reset();
  if (FAILED(CompileCs(kLowPassCs, "main", &blob))) {
    if (error_message) snprintf(error_message, error_message_size, "compile micro-detail-lowpass shader failed");
    return false;
  }
  ctx->device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &ctx->cs_lowpass);
  blob.Reset();
  if (FAILED(CompileCs(kDebandCs, "main", &blob))) {
    if (error_message) snprintf(error_message, error_message_size, "compile deband shader failed");
    return false;
  }
  ctx->device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &ctx->cs_deband);
  blob.Reset();
  if (FAILED(CompileCs(kShimmerCs, "main", &blob))) {
    if (error_message) snprintf(error_message, error_message_size, "compile temporal-shimmer shader failed");
    return false;
  }
  ctx->device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &ctx->cs_shimmer);
  blob.Reset();

  if (!ctx->cs_motion_adaptive || !ctx->cs_lowpass || !ctx->cs_deband || !ctx->cs_shimmer) {
    if (error_message) snprintf(error_message, error_message_size, "create compute shader failed");
    return false;
  }

  if (!CreateFloatTexture(ctx, &ctx->work_a_tex, &ctx->work_a_srv, &ctx->work_a_uav) ||
      !CreateFloatTexture(ctx, &ctx->work_b_tex, &ctx->work_b_srv, &ctx->work_b_uav) ||
      !CreateFloatTexture(ctx, &ctx->prev_prequality_tex, &ctx->prev_prequality_srv, nullptr) ||
      !CreateFloatTexture(ctx, &ctx->prev_preprocessed_tex, &ctx->prev_preprocessed_srv, nullptr)) {
    if (error_message) snprintf(error_message, error_message_size, "create processing textures failed");
    return false;
  }

  D3D11_BUFFER_DESC cbd{};
  cbd.ByteWidth = 16;
  cbd.Usage = D3D11_USAGE_DYNAMIC;
  cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
  cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
  if (FAILED(ctx->device->CreateBuffer(&cbd, nullptr, &ctx->effect_cb))) {
    if (error_message) snprintf(error_message, error_message_size, "create effect constant buffer failed");
    return false;
  }

  ctx->first_processed = true;
  ctx->processing_configured = true;
  return true;
}

bool GpuBlendHasEnabledEffects(GpuBlendContext* ctx) {
  return ctx && !ctx->enabled_effects.empty();
}

static bool UpdateEffectParams(GpuBlendContext* ctx, const MmodEffectDescV1& e) {
  D3D11_MAPPED_SUBRESOURCE map{};
  if (FAILED(ctx->context->Map(ctx->effect_cb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &map)))
    return false;
  float* f = static_cast<float*>(map.pData);
  f[0] = e.p0;
  f[1] = e.p1;
  f[2] = e.p2;
  f[3] = e.p3;
  ctx->context->Unmap(ctx->effect_cb.Get(), 0);
  return true;
}

bool GpuBlendProcess(GpuBlendContext* ctx) {
  if (!ctx || ctx->enabled_effects.empty()) return true;

  if (ctx->first_processed) {
    // First processed frame: no motion/reference history, seed with current.
    ctx->context->CopyResource(ctx->prev_prequality_tex.Get(), ctx->acc_tex.Get());
    ctx->context->CopyResource(ctx->prev_preprocessed_tex.Get(), ctx->acc_tex.Get());
    ctx->first_processed = false;
  }

  // work_a holds the unprocessed current frame; effects ping-pong to work_b.
  ctx->context->CopyResource(ctx->work_a_tex.Get(), ctx->acc_tex.Get());

  ID3D11Texture2D* cur = ctx->work_a_tex.Get();
  ID3D11Texture2D* dst = ctx->work_b_tex.Get();
  ID3D11ShaderResourceView* cur_srv = ctx->work_a_srv.Get();
  ID3D11ShaderResourceView* dst_srv = ctx->work_b_srv.Get();
  ID3D11UnorderedAccessView* dst_uav = ctx->work_b_uav.Get();

  const UINT gx = (ctx->width + 15) / 16;
  const UINT gy = (ctx->height + 15) / 16;

  for (const auto& e : ctx->enabled_effects) {
    ID3D11ComputeShader* cs = nullptr;
    switch (e.effect_type) {
      case MmodEffect_MotionAdaptiveDetail: cs = ctx->cs_motion_adaptive.Get(); break;
      case MmodEffect_MicroDetailLowPass: cs = ctx->cs_lowpass.Get(); break;
      case MmodEffect_DebandNoDither: cs = ctx->cs_deband.Get(); break;
      case MmodEffect_TemporalShimmerReduction: cs = ctx->cs_shimmer.Get(); break;
      default: continue; /* unknown effect: ignore safely */
    }
    if (!cs || !UpdateEffectParams(ctx, e)) return false;

    ID3D11ShaderResourceView* prev_srv = (e.effect_type == MmodEffect_TemporalShimmerReduction)
        ? ctx->prev_preprocessed_srv.Get()
        : ctx->prev_prequality_srv.Get();

    ctx->context->CSSetShader(cs, nullptr, 0);
    ID3D11ShaderResourceView* srvs[] = { cur_srv, prev_srv };
    ctx->context->CSSetShaderResources(0, 2, srvs);
    ID3D11UnorderedAccessView* uavs[] = { dst_uav };
    ctx->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
    ID3D11Buffer* cbs[] = { ctx->effect_cb.Get() };
    ctx->context->CSSetConstantBuffers(0, 1, cbs);
    ctx->context->Dispatch(gx, gy, 1);

    ID3D11ShaderResourceView* nullsrv[] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* nulluav[] = { nullptr };
    ctx->context->CSSetShaderResources(0, 2, nullsrv);
    ctx->context->CSSetUnorderedAccessViews(0, 1, nulluav, nullptr);

    std::swap(cur, dst);
    std::swap(cur_srv, dst_srv);
    dst_uav = (dst == ctx->work_a_tex.Get()) ? ctx->work_a_uav.Get() : ctx->work_b_uav.Get();
  }

  // acc_tex still holds the unprocessed frame: save it as prev_prequality,
  // then write the processed result into acc_tex and save prev_preprocessed.
  ctx->context->CopyResource(ctx->prev_prequality_tex.Get(), ctx->acc_tex.Get());
  ctx->context->CopyResource(ctx->acc_tex.Get(), cur);
  ctx->context->CopyResource(ctx->prev_preprocessed_tex.Get(), ctx->acc_tex.Get());
  return true;
}
