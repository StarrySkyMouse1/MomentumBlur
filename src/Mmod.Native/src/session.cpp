#include "mmod_native.h"
#include "gpu_blend.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

struct MmodSession {
  int32_t width = 0;
  int32_t height = 0;
  int32_t blend_frames = 1;
  float exposure = 0.5f;
  int32_t output_fps = 60;
  int32_t encoder = MmodEncoder_Auto;
  std::wstring output_path;

  int32_t frames_submitted = 0;
  int32_t frames_output = 0;
  int32_t frames_total_estimate = 0;
  std::vector<float> weights;
  std::vector<float> accumulator; // RGB float 0-255 (CPU fallback)
  std::vector<uint8_t> output_bgra;

  GpuBlendContext* gpu = nullptr;
  bool use_gpu = false;

  ComPtr<IMFSinkWriter> writer;
  DWORD writer_stream = 0;
  bool writer_ready = false;
  bool finished = false;
  LONGLONG sample_time = 0;
  LONGLONG sample_duration = 0;
};

static std::vector<float> BuildWeights(int blend_frames, float exposure) {
  std::vector<float> weights(static_cast<size_t>(std::max(1, blend_frames)), 1.0f);
  if (blend_frames <= 1) {
    return weights;
  }

  const float sigma = std::max(0.05f, exposure) * static_cast<float>(blend_frames) * 0.5f;
  const float center = (static_cast<float>(blend_frames) - 1.0f) * 0.5f;
  float sum = 0.0f;
  for (int i = 0; i < blend_frames; ++i) {
    const float x = static_cast<float>(i) - center;
    weights[static_cast<size_t>(i)] = std::exp(-(x * x) / (2.0f * sigma * sigma));
    sum += weights[static_cast<size_t>(i)];
  }
  if (sum > 0.0f) {
    for (float& w : weights) {
      w /= sum;
    }
  }
  return weights;
}

static void ClearAccumulator(MmodSession* session) {
  if (session->use_gpu && session->gpu) {
    GpuBlendResetWindow(session->gpu);
    return;
  }
  std::fill(session->accumulator.begin(), session->accumulator.end(), 0.0f);
}

static void AccumulateFrame(MmodSession* session, const uint8_t* bgra, int32_t stride, float weight) {
  if (session->use_gpu && session->gpu) {
    GpuBlendAccumulate(session->gpu, bgra, stride, weight);
    return;
  }
  const int width = session->width;
  const int height = session->height;
  for (int y = 0; y < height; ++y) {
    const uint8_t* row = bgra + static_cast<size_t>(y) * static_cast<size_t>(stride);
    for (int x = 0; x < width; ++x) {
      const size_t pi = (static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)) * 3u;
      const size_t bi = static_cast<size_t>(x) * 4u;
      session->accumulator[pi + 0] += static_cast<float>(row[bi + 2]) * weight;
      session->accumulator[pi + 1] += static_cast<float>(row[bi + 1]) * weight;
      session->accumulator[pi + 2] += static_cast<float>(row[bi + 0]) * weight;
    }
  }
}

static void PackOutputBgra(MmodSession* session) {
  if (session->use_gpu && session->gpu) {
    GpuBlendPack(session->gpu, session->output_bgra);
    return;
  }
  const size_t pixels = static_cast<size_t>(session->width) * static_cast<size_t>(session->height);
  session->output_bgra.resize(pixels * 4u);
  for (size_t i = 0; i < pixels; ++i) {
    const float r = std::clamp(session->accumulator[i * 3u + 0], 0.0f, 255.0f);
    const float g = std::clamp(session->accumulator[i * 3u + 1], 0.0f, 255.0f);
    const float b = std::clamp(session->accumulator[i * 3u + 2], 0.0f, 255.0f);
    session->output_bgra[i * 4u + 0] = static_cast<uint8_t>(b);
    session->output_bgra[i * 4u + 1] = static_cast<uint8_t>(g);
    session->output_bgra[i * 4u + 2] = static_cast<uint8_t>(r);
    session->output_bgra[i * 4u + 3] = 255;
  }
}

static HRESULT ConfigureSinkWriter(MmodSession* session) {
  ComPtr<IMFAttributes> attrs;
  HRESULT hr = MFCreateAttributes(&attrs, 2);
  if (FAILED(hr)) return hr;
  attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
  attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);

  hr = MFCreateSinkWriterFromURL(session->output_path.c_str(), nullptr, attrs.Get(), &session->writer);
  if (FAILED(hr)) {
    return hr;
  }

  ComPtr<IMFMediaType> out_type;
  hr = MFCreateMediaType(&out_type);
  if (FAILED(hr)) return hr;
  hr = out_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
  if (FAILED(hr)) return hr;
  hr = out_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
  if (FAILED(hr)) return hr;
  // 120 Mbps is about 900 MB per minute before the small MP4 container overhead.
  // Media Foundation may use a hardware H.264 transform when available and
  // otherwise falls back to an installed software transform.
  hr = out_type->SetUINT32(MF_MT_AVG_BITRATE, 120'000'000);
  if (FAILED(hr)) return hr;
  hr = out_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeSize(out_type.Get(), MF_MT_FRAME_SIZE, session->width, session->height);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeRatio(out_type.Get(), MF_MT_FRAME_RATE, session->output_fps, 1);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeRatio(out_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
  if (FAILED(hr)) return hr;

  hr = session->writer->AddStream(out_type.Get(), &session->writer_stream);
  if (FAILED(hr)) return hr;

  ComPtr<IMFMediaType> in_type;
  hr = MFCreateMediaType(&in_type);
  if (FAILED(hr)) return hr;
  hr = in_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
  if (FAILED(hr)) return hr;
  hr = in_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
  if (FAILED(hr)) return hr;
  hr = in_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeSize(in_type.Get(), MF_MT_FRAME_SIZE, session->width, session->height);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeRatio(in_type.Get(), MF_MT_FRAME_RATE, session->output_fps, 1);
  if (FAILED(hr)) return hr;
  hr = MFSetAttributeRatio(in_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
  if (FAILED(hr)) return hr;
  hr = in_type->SetUINT32(MF_MT_DEFAULT_STRIDE, session->width * 4);
  if (FAILED(hr)) return hr;

  hr = session->writer->SetInputMediaType(session->writer_stream, in_type.Get(), nullptr);
  if (FAILED(hr)) return hr;

  hr = session->writer->BeginWriting();
  if (FAILED(hr)) return hr;

  session->sample_duration = 10'000'000LL / std::max(1, session->output_fps);
  session->sample_time = 0;
  session->writer_ready = true;
  return S_OK;
}

static HRESULT WriteOutputFrame(MmodSession* session) {
  if (!session->writer_ready) {
    const HRESULT hr = ConfigureSinkWriter(session);
    if (FAILED(hr)) {
      return hr;
    }
  }

  PackOutputBgra(session);

  const DWORD buffer_size = static_cast<DWORD>(session->output_bgra.size());
  ComPtr<IMFMediaBuffer> buffer;
  HRESULT hr = MFCreateMemoryBuffer(buffer_size, &buffer);
  if (FAILED(hr)) return hr;

  BYTE* data = nullptr;
  hr = buffer->Lock(&data, nullptr, nullptr);
  if (FAILED(hr)) return hr;
  // Positive MF_MT_DEFAULT_STRIDE means top-down; keep accumulator orientation.
  std::memcpy(data, session->output_bgra.data(), buffer_size);
  buffer->Unlock();
  hr = buffer->SetCurrentLength(buffer_size);
  if (FAILED(hr)) return hr;

  ComPtr<IMFSample> sample;
  hr = MFCreateSample(&sample);
  if (FAILED(hr)) return hr;
  hr = sample->AddBuffer(buffer.Get());
  if (FAILED(hr)) return hr;
  hr = sample->SetSampleTime(session->sample_time);
  if (FAILED(hr)) return hr;
  hr = sample->SetSampleDuration(session->sample_duration);
  if (FAILED(hr)) return hr;

  hr = session->writer->WriteSample(session->writer_stream, sample.Get());
  if (FAILED(hr)) return hr;

  session->sample_time += session->sample_duration;
  session->frames_output += 1;
  return S_OK;
}

extern "C" MMOD_API const char* mmod_error_string(int32_t error) {
  switch (error) {
    case MmodError_Ok: return "ok";
    case MmodError_InvalidArg: return "invalid argument";
    case MmodError_OutOfMemory: return "out of memory";
    case MmodError_NotImplemented: return "not implemented";
    case MmodError_SubmitFailed: return "submit failed";
    case MmodError_EncodeFailed: return "encode failed";
    case MmodError_IoFailed: return "io failed";
    case MmodError_DecodeFailed: return "decode failed";
    case MmodError_ComFailed: return "com/media foundation failed";
    default: return "unknown";
  }
}

extern "C" MMOD_API MmodSession* mmod_session_create(const MmodSessionDesc* desc, int32_t* out_error) {
  if (out_error) *out_error = MmodError_Ok;
  if (!desc || desc->width <= 0 || desc->height <= 0 || !desc->output_path) {
    if (out_error) *out_error = MmodError_InvalidArg;
    return nullptr;
  }

  HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  const bool need_uninit = SUCCEEDED(hr);
  if (hr == RPC_E_CHANGED_MODE) {
    // Already initialized differently; continue.
  } else if (FAILED(hr) && hr != S_FALSE) {
    if (out_error) *out_error = MmodError_ComFailed;
    return nullptr;
  }

  hr = MFStartup(MF_VERSION);
  if (FAILED(hr)) {
    if (need_uninit) CoUninitialize();
    if (out_error) *out_error = MmodError_ComFailed;
    return nullptr;
  }

  auto session = std::make_unique<MmodSession>();
  session->width = desc->width;
  session->height = desc->height;
  session->blend_frames = std::max(1, desc->blend_frames);
  session->exposure = desc->exposure;
  session->output_fps = desc->output_fps > 0 ? desc->output_fps : 60;
  session->encoder = desc->encoder;
  session->output_path = desc->output_path;
  session->weights = BuildWeights(session->blend_frames, session->exposure);
  session->accumulator.assign(static_cast<size_t>(session->width) * static_cast<size_t>(session->height) * 3u, 0.0f);
  session->gpu = GpuBlendCreate(session->width, session->height, session->blend_frames);
  session->use_gpu = session->gpu != nullptr;

  (void)need_uninit;
  return session.release();
}

extern "C" MMOD_API int32_t mmod_session_submit_bgra(MmodSession* session, const uint8_t* bgra, int32_t stride) {
  if (!session || !bgra || session->finished) return MmodError_InvalidArg;
  if (stride < session->width * 4) return MmodError_InvalidArg;

  const int index_in_window = session->frames_submitted % session->blend_frames;
  if (index_in_window == 0) {
    ClearAccumulator(session);
  }

  AccumulateFrame(session, bgra, stride, session->weights[static_cast<size_t>(index_in_window)]);
  session->frames_submitted += 1;

  if ((session->frames_submitted % session->blend_frames) == 0) {
    if (FAILED(WriteOutputFrame(session))) {
      return MmodError_EncodeFailed;
    }
  }

  return MmodError_Ok;
}

extern "C" MMOD_API int32_t mmod_session_finish(MmodSession* session) {
  if (!session) return MmodError_InvalidArg;
  session->finished = true;

  // Flush partial window by re-using last weights proportionally if needed: drop remainder.
  if (session->writer_ready && session->writer) {
    const HRESULT hr = session->writer->Finalize();
    session->writer.Reset();
    session->writer_ready = false;
    if (FAILED(hr)) return MmodError_EncodeFailed;
  } else if (session->frames_output == 0) {
    return MmodError_EncodeFailed;
  }

  return MmodError_Ok;
}

extern "C" MMOD_API void mmod_session_destroy(MmodSession* session) {
  if (!session) return;
  if (session->writer) {
    session->writer.Reset();
  }
  if (session->gpu) {
    GpuBlendDestroy(session->gpu);
    session->gpu = nullptr;
  }
  delete session;
  MFShutdown();
}

extern "C" MMOD_API int32_t mmod_session_get_progress(MmodSession* session, int32_t* out_done, int32_t* out_total) {
  if (!session) return MmodError_InvalidArg;
  if (out_done) *out_done = session->frames_output;
  if (out_total) {
    if (session->frames_total_estimate > 0) {
      *out_total = session->frames_total_estimate;
    } else {
      *out_total = session->frames_submitted / std::max(1, session->blend_frames);
      if (*out_total < session->frames_output) *out_total = session->frames_output;
    }
  }
  return MmodError_Ok;
}

static HRESULT ConfigureSourceReaderRgb32(IMFSourceReader* reader, UINT32* width, UINT32* height) {
  ComPtr<IMFMediaType> partial;
  HRESULT hr = MFCreateMediaType(&partial);
  if (FAILED(hr)) return hr;
  hr = partial->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
  if (FAILED(hr)) return hr;
  hr = partial->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
  if (FAILED(hr)) return hr;
  hr = reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, partial.Get());
  if (FAILED(hr)) return hr;

  ComPtr<IMFMediaType> current;
  hr = reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &current);
  if (FAILED(hr)) return hr;
  hr = MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, width, height);
  if (FAILED(hr)) return hr;
  return S_OK;
}

extern "C" MMOD_API int32_t mmod_process_video_file(
    const wchar_t* input_path,
    const MmodSessionDesc* desc,
    MmodProgressFn progress,
    void* progress_user,
    int32_t* out_error) {
  if (out_error) *out_error = MmodError_Ok;
  if (!input_path || !desc || !desc->output_path) {
    if (out_error) *out_error = MmodError_InvalidArg;
    return MmodError_InvalidArg;
  }

  HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  if (FAILED(hr) && hr != S_FALSE && hr != RPC_E_CHANGED_MODE) {
    if (out_error) *out_error = MmodError_ComFailed;
    return MmodError_ComFailed;
  }

  hr = MFStartup(MF_VERSION);
  if (FAILED(hr)) {
    if (out_error) *out_error = MmodError_ComFailed;
    return MmodError_ComFailed;
  }

  ComPtr<IMFAttributes> attrs;
  hr = MFCreateAttributes(&attrs, 2);
  if (FAILED(hr)) {
    MFShutdown();
    if (out_error) *out_error = MmodError_ComFailed;
    return MmodError_ComFailed;
  }
  attrs->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
  attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);

  ComPtr<IMFSourceReader> reader;
  hr = MFCreateSourceReaderFromURL(input_path, attrs.Get(), &reader);
  if (FAILED(hr)) {
    MFShutdown();
    if (out_error) *out_error = MmodError_DecodeFailed;
    return MmodError_DecodeFailed;
  }

  reader->SetStreamSelection((DWORD)MF_SOURCE_READER_ALL_STREAMS, FALSE);
  reader->SetStreamSelection((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);

  UINT32 width = 0;
  UINT32 height = 0;
  hr = ConfigureSourceReaderRgb32(reader.Get(), &width, &height);
  if (FAILED(hr) || width == 0 || height == 0) {
    // Fallback: read native type size then force RGB32 again.
    ComPtr<IMFMediaType> native;
    if (SUCCEEDED(reader->GetNativeMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &native))) {
      MFGetAttributeSize(native.Get(), MF_MT_FRAME_SIZE, &width, &height);
    }
    hr = ConfigureSourceReaderRgb32(reader.Get(), &width, &height);
  }
  if (FAILED(hr) || width == 0 || height == 0) {
    MFShutdown();
    if (out_error) *out_error = MmodError_DecodeFailed;
    return MmodError_DecodeFailed;
  }

  MmodSessionDesc local = *desc;
  local.width = static_cast<int32_t>(width);
  local.height = static_cast<int32_t>(height);
  auto session = std::make_unique<MmodSession>();
  session->width = local.width;
  session->height = local.height;
  session->blend_frames = std::max(1, local.blend_frames);
  session->exposure = local.exposure;
  session->output_fps = local.output_fps > 0 ? local.output_fps : 60;
  session->encoder = local.encoder;
  session->output_path = local.output_path;
  session->weights = BuildWeights(session->blend_frames, session->exposure);
  session->accumulator.assign(static_cast<size_t>(session->width) * static_cast<size_t>(session->height) * 3u, 0.0f);
  session->gpu = GpuBlendCreate(session->width, session->height, session->blend_frames);
  session->use_gpu = session->gpu != nullptr;

  // Estimate total output frames from duration if available.
  PROPVARIANT var{};
  PropVariantInit(&var);
  if (SUCCEEDED(reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, &var)) &&
      var.vt == VT_UI8) {
    const double seconds = static_cast<double>(var.uhVal.QuadPart) / 10'000'000.0;
    const int approx_input = static_cast<int>(seconds * 120.0); // rough; refined while reading
    session->frames_total_estimate = std::max(1, approx_input / session->blend_frames);
  }
  PropVariantClear(&var);

  std::vector<uint8_t> frame_bgra(static_cast<size_t>(width) * height * 4u);
  bool done = false;
  while (!done) {
    DWORD stream_index = 0;
    DWORD flags = 0;
    LONGLONG timestamp = 0;
    ComPtr<IMFSample> sample;
    hr = reader->ReadSample(
        (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM,
        0,
        &stream_index,
        &flags,
        &timestamp,
        &sample);
    if (FAILED(hr)) {
      mmod_session_destroy(session.release());
      if (out_error) *out_error = MmodError_DecodeFailed;
      return MmodError_DecodeFailed;
    }
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
      done = true;
      break;
    }
    if (!sample) {
      continue;
    }

    ComPtr<IMFMediaBuffer> buffer;
    hr = sample->ConvertToContiguousBuffer(&buffer);
    if (FAILED(hr)) continue;

    BYTE* data = nullptr;
    DWORD max_len = 0;
    DWORD cur_len = 0;
    hr = buffer->Lock(&data, &max_len, &cur_len);
    if (FAILED(hr)) continue;

    const int stride = static_cast<int>(width) * 4;
    const DWORD expected = static_cast<DWORD>(stride * height);
    if (cur_len >= expected) {
      // Source may be bottom-up; convert to top-down BGRA for accumulator.
      for (UINT32 y = 0; y < height; ++y) {
        const BYTE* src = data + static_cast<size_t>(height - 1 - y) * static_cast<size_t>(stride);
        uint8_t* dst = frame_bgra.data() + static_cast<size_t>(y) * static_cast<size_t>(stride);
        std::memcpy(dst, src, static_cast<size_t>(stride));
      }
      buffer->Unlock();

      const int32_t submit = mmod_session_submit_bgra(session.get(), frame_bgra.data(), stride);
      if (submit != MmodError_Ok) {
        mmod_session_destroy(session.release());
        if (out_error) *out_error = submit;
        return submit;
      }
      if (progress) {
        progress(progress_user, session->frames_output, std::max(session->frames_output, session->frames_total_estimate));
      }
    } else {
      buffer->Unlock();
    }
  }

  const int32_t finish = mmod_session_finish(session.get());
  mmod_session_destroy(session.release());
  if (finish != MmodError_Ok) {
    if (out_error) *out_error = finish;
    return finish;
  }
  if (out_error) *out_error = MmodError_Ok;
  return MmodError_Ok;
}
