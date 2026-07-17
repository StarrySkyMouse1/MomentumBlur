#include "gpu_blend.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <cstring>
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

HRESULT CompileCs(const char* src, const char* entry, ID3DBlob** blob) {
  ComPtr<ID3DBlob> error;
  HRESULT hr = D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, entry, "cs_5_0",
                          D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, blob, &error);
  return hr;
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
};

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
