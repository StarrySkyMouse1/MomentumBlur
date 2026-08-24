#include "frame_processing.h"

#include <algorithm>
#include <cmath>

namespace {

constexpr int kStageTemporal = 0;
constexpr int kStageMotionAwareSpatial = 1;
constexpr int kStageGlobalSpatial = 2;

int EffectStage(int32_t effect_type) {
  switch (effect_type) {
    case MmodEffect_TemporalShimmerReduction: return kStageTemporal;
    case MmodEffect_MotionAdaptiveDetail: return kStageMotionAwareSpatial;
    case MmodEffect_MicroDetailLowPass:
    case MmodEffect_DebandNoDither: return kStageGlobalSpatial;
    default: return 3; /* unknown: run last, ignored below anyway */
  }
}

float Clamp01(float v) { return v < 0.f ? 0.f : (v > 1.f ? 1.f : v); }

float SmoothStep(float e0, float e1, float x) {
  if (e1 <= e0) return 0.f;
  const float t = Clamp01((x - e0) / (e1 - e0));
  return t * t * (3.f - 2.f * t);
}

float Luma(const float* rgb) {
  return 0.2126f * rgb[0] + 0.7152f * rgb[1] + 0.0722f * rgb[2];
}

struct Img {
  std::vector<float> data;
  int width = 0;
  int height = 0;

  void Resize(int w, int h) {
    width = w;
    height = h;
    data.assign(static_cast<size_t>(w) * static_cast<size_t>(h) * 3u, 0.f);
  }

  const float* Pixel(int x, int y) const {
    x = std::max(0, std::min(width - 1, x));
    y = std::max(0, std::min(height - 1, y));
    return &data[(static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)) * 3u];
  }

  float* Pixel(int x, int y) {
    x = std::max(0, std::min(width - 1, x));
    y = std::max(0, std::min(height - 1, y));
    return &data[(static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)) * 3u];
  }

  void CopyFrom(const float* src) {
    std::copy(src, src + data.size(), data.begin());
  }

  void CopyTo(float* dst) const {
    std::copy(data.begin(), data.end(), dst);
  }
};

void BoxBlur(const Img& img, int x, int y, int radius, float out[3]) {
  float acc[3] = {0.f, 0.f, 0.f};
  const int span = 2 * radius + 1;
  for (int dy = -radius; dy <= radius; ++dy) {
    for (int dx = -radius; dx <= radius; ++dx) {
      const float* p = img.Pixel(x + dx, y + dy);
      acc[0] += p[0];
      acc[1] += p[1];
      acc[2] += p[2];
    }
  }
  const float inv = 1.f / static_cast<float>(span * span);
  out[0] = acc[0] * inv;
  out[1] = acc[1] * inv;
  out[2] = acc[2] * inv;
}

/* p0=strength p1=motion-threshold p2=edge-protection */
void ApplyMotionAdaptiveDetail(const Img& cur, const Img& prev_prequality, Img& dst,
                               const MmodEffectDescV1& e) {
  const float strength = e.p0;
  const float threshold = std::max(0.001f, e.p1);
  const float edge_protection = Clamp01(e.p2);
  const int w = cur.width;
  const int h = cur.height;

  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const float* c = cur.Pixel(x, y);
      const float* pv = prev_prequality.Pixel(x, y);
      const float yc = Luma(c);
      const float yp = Luma(pv);

      float motion = std::abs(yc - yp) / 255.f;
      const float mask = SmoothStep(threshold, threshold * 2.5f, motion);

      float blur[3];
      BoxBlur(cur, x, y, 1, blur);

      const float lx0 = Luma(cur.Pixel(x - 1, y));
      const float lx1 = Luma(cur.Pixel(x + 1, y));
      const float ly0 = Luma(cur.Pixel(x, y - 1));
      const float ly1 = Luma(cur.Pixel(x, y + 1));
      const float gx = std::abs(lx1 - lx0);
      const float gy = std::abs(ly1 - ly0);
      const float edge = std::sqrt(gx * gx + gy * gy) / 255.f;
      const float edge_mask = Clamp01(edge / 0.6f);
      const float protect = 1.f - edge_mask * edge_protection;

      const float t = mask * strength * protect;
      float* out = dst.Pixel(x, y);
      out[0] = c[0] + (blur[0] - c[0]) * t;
      out[1] = c[1] + (blur[1] - c[1]) * t;
      out[2] = c[2] + (blur[2] - c[2]) * t;
    }
  }
}

/* p0=strength p1=radius (1..2) */
void ApplyMicroDetailLowPass(const Img& cur, Img& dst, const MmodEffectDescV1& e) {
  const float strength = Clamp01(e.p0);
  const int radius = e.p1 >= 1.5f ? 2 : 1;
  const int w = cur.width;
  const int h = cur.height;

  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const float* c = cur.Pixel(x, y);
      float blur[3];
      BoxBlur(cur, x, y, radius, blur);
      float* out = dst.Pixel(x, y);
      out[0] = c[0] + (blur[0] - c[0]) * strength;
      out[1] = c[1] + (blur[1] - c[1]) * strength;
      out[2] = c[2] + (blur[2] - c[2]) * strength;
    }
  }
}

/* p0=strength p1=threshold */
void ApplyDebandNoDither(const Img& cur, Img& dst, const MmodEffectDescV1& e) {
  const float strength = Clamp01(e.p0);
  const float threshold = std::max(0.001f, e.p1);
  const int w = cur.width;
  const int h = cur.height;

  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const float* c = cur.Pixel(x, y);
      float mean[3];
      BoxBlur(cur, x, y, 2, mean);

      float center_diff = 0.f;
      for (int ch = 0; ch < 3; ++ch) {
        center_diff = std::max(center_diff, std::abs(c[ch] - mean[ch]) / 255.f);
      }
      const float flat = 1.f - SmoothStep(threshold, threshold * 3.f, center_diff);
      const float t = strength * flat;

      float* out = dst.Pixel(x, y);
      out[0] = c[0] + (mean[0] - c[0]) * t;
      out[1] = c[1] + (mean[1] - c[1]) * t;
      out[2] = c[2] + (mean[2] - c[2]) * t;
    }
  }
}

/* p0=strength p1=temporal-threshold; mixes toward prev_preprocessed only for
   high-frequency pixels with a small temporal delta. */
void ApplyTemporalShimmerReduction(const Img& cur, const Img& prev_preprocessed, Img& dst,
                                   const MmodEffectDescV1& e) {
  const float strength = Clamp01(e.p0);
  const float threshold = std::max(0.001f, e.p1);
  const int w = cur.width;
  const int h = cur.height;

  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const float* c = cur.Pixel(x, y);
      const float* pv = prev_preprocessed.Pixel(x, y);

      float diff = 0.f;
      for (int ch = 0; ch < 3; ++ch) {
        diff = std::max(diff, std::abs(c[ch] - pv[ch]) / 255.f);
      }
      const float k = 1.f - SmoothStep(threshold * 0.5f, threshold * 2.f, diff);

      float blur[3];
      BoxBlur(cur, x, y, 1, blur);
      const float hf = std::abs(Luma(c) - Luma(blur)) / 255.f;
      const float hf_mask = SmoothStep(0.02f, 0.10f, hf);

      const float t = strength * k * hf_mask;
      float* out = dst.Pixel(x, y);
      out[0] = c[0] + (pv[0] - c[0]) * t;
      out[1] = c[1] + (pv[1] - c[1]) * t;
      out[2] = c[2] + (pv[2] - c[2]) * t;
    }
  }
}

void ApplyOne(const Img& cur, const Img& prev_prequality, const Img& prev_preprocessed,
              Img& dst, const MmodEffectDescV1& e) {
  switch (e.effect_type) {
    case MmodEffect_MotionAdaptiveDetail:
      ApplyMotionAdaptiveDetail(cur, prev_prequality, dst, e);
      break;
    case MmodEffect_MicroDetailLowPass:
      ApplyMicroDetailLowPass(cur, dst, e);
      break;
    case MmodEffect_DebandNoDither:
      ApplyDebandNoDither(cur, dst, e);
      break;
    case MmodEffect_TemporalShimmerReduction:
      ApplyTemporalShimmerReduction(cur, prev_preprocessed, dst, e);
      break;
    default:
      /* unknown effect: ignore safely */
      break;
  }
}

} // namespace

struct FrameProcessingState {
  int width = 0;
  int height = 0;
  bool first = true;
  Img prev_prequality;    /* previous unprocessed accumulated frame */
  Img prev_preprocessed;  /* previous processed output frame */
  Img work_a;             /* ping-pong buffers */
  Img work_b;
};

FrameProcessingState* FrameProcessingCreate(int width, int height) {
  if (width <= 0 || height <= 0) return nullptr;
  auto* state = new FrameProcessingState();
  state->width = width;
  state->height = height;
  state->prev_prequality.Resize(width, height);
  state->prev_preprocessed.Resize(width, height);
  state->work_a.Resize(width, height);
  state->work_b.Resize(width, height);
  return state;
}

void FrameProcessingDestroy(FrameProcessingState* state) {
  delete state;
}

bool FrameProcessingApply(FrameProcessingState* state,
                          float* rgb,
                          const MmodEffectDescV1* effects,
                          int32_t effect_count) {
  if (!state || !rgb || !effects || effect_count <= 0) return true;

  /* Sort enabled effects by (stage, order) — stable for equal keys. */
  std::vector<MmodEffectDescV1> sorted;
  sorted.reserve(static_cast<size_t>(effect_count));
  for (int32_t i = 0; i < effect_count; ++i) {
    if (effects[i].enabled && effects[i].effect_type != MmodEffect_None) {
      sorted.push_back(effects[i]);
    }
  }
  std::stable_sort(sorted.begin(), sorted.end(),
                   [](const MmodEffectDescV1& a, const MmodEffectDescV1& b) {
                     const int sa = EffectStage(a.effect_type);
                     const int sb = EffectStage(b.effect_type);
                     if (sa != sb) return sa < sb;
                     return a.order < b.order;
                   });
  if (sorted.empty()) return true;

  if (state->first) {
    state->prev_prequality.CopyFrom(rgb);
    state->prev_preprocessed.CopyFrom(rgb);
    state->first = false;
  }

  /* rgb is the unprocessed current frame and stays untouched until the final
     copy-back, so prev_prequality can be refreshed from it after the pass. */
  state->work_a.CopyFrom(rgb);

  const Img* cur = &state->work_a;
  Img* dst = &state->work_b;
  for (const auto& e : sorted) {
    ApplyOne(*cur, state->prev_prequality, state->prev_preprocessed, *dst, e);
    const Img* next_cur = dst;
    dst = const_cast<Img*>(cur);
    cur = next_cur;
  }

  /* Result is in `cur`. rgb still holds the unprocessed current frame here:
     save it as prev_prequality for the next frame BEFORE overwriting rgb. */
  state->prev_prequality.CopyFrom(rgb);
  cur->CopyTo(rgb);
  state->prev_preprocessed.CopyFrom(rgb); /* processed current for the next frame */
  return true;
}
