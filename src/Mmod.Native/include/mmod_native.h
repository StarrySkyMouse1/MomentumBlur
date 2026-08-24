#pragma once

#include <stdint.h>

#ifdef _WIN32
#  ifdef MMOD_NATIVE_EXPORTS
#    define MMOD_API __declspec(dllexport)
#  else
#    define MMOD_API __declspec(dllimport)
#  endif
#else
#  define MMOD_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MmodSession MmodSession;

enum MmodEncoderKind {
  MmodEncoder_Auto = 0,
  MmodEncoder_Nvenc = 1,
  MmodEncoder_Amf = 2
};

/* Motion blur weight window mode. Legacy keeps historical Exposure semantics. */
enum MmodMotionBlurMode {
  MmodMotionBlur_LegacyGaussianExposure = 0,
  MmodMotionBlur_ShutterAngle = 1
};

/* Stable effect type ids. Must match NativeProcessingMapper.NativeEffectType in C#. */
enum MmodEffectType {
  MmodEffect_None = 0,
  MmodEffect_MotionAdaptiveDetail = 1,
  MmodEffect_MicroDetailLowPass = 2,
  MmodEffect_DebandNoDither = 3,
  MmodEffect_TemporalShimmerReduction = 4
};

enum MmodError {
  MmodError_Ok = 0,
  MmodError_InvalidArg = 1,
  MmodError_OutOfMemory = 2,
  MmodError_NotImplemented = 3,
  MmodError_SubmitFailed = 4,
  MmodError_EncodeFailed = 5,
  MmodError_IoFailed = 6,
  MmodError_DecodeFailed = 7,
  MmodError_ComFailed = 8,
  MmodError_ProcessingInitFailed = 9,
  MmodError_ProcessingFailed = 10,
  MmodError_UnsupportedEffect = 11,
  MmodError_ProcessingFallbackCpu = 12 /* warning: session created, GPU processing fell back to CPU */
};

typedef struct MmodEffectDescV1 {
  int32_t effect_type; /* MmodEffectType */
  int32_t enabled;     /* 0/1 */
  int32_t order;       /* ordering within the same stage only */
  int32_t reserved;

  float p0;
  float p1;
  float p2;
  float p3;
  float p4;
  float p5;
  float p6;
  float p7;
} MmodEffectDescV1;

typedef struct MmodSessionDesc {
  int32_t width;
  int32_t height;
  int32_t blend_frames;
  float exposure;
  int32_t output_fps;
  int32_t encoder; /* MmodEncoderKind */
  const wchar_t* output_path;

  /* New fields (appended; offsets of fields above are unchanged). */
  const MmodEffectDescV1* effects; /* may be NULL */
  int32_t effect_count;
  int32_t target_bitrate;          /* >0 to override auto estimation (capped), 0 = auto */
  int32_t motion_blur_mode;        /* MmodMotionBlurMode */
  float shutter_angle;             /* 180..360, used when motion_blur_mode == ShutterAngle */
} MmodSessionDesc;

typedef void (*MmodProgressFn)(void* user, int32_t done, int32_t total);

MMOD_API MmodSession* mmod_session_create(const MmodSessionDesc* desc, int32_t* out_error);
MMOD_API int32_t mmod_session_submit_bgra(MmodSession* session, const uint8_t* bgra, int32_t stride);
MMOD_API int32_t mmod_session_finish(MmodSession* session);
MMOD_API void mmod_session_destroy(MmodSession* session);
MMOD_API int32_t mmod_session_get_progress(MmodSession* session, int32_t* out_done, int32_t* out_total);

/* Diagnostics: whether quality effects are enabled and whether the session fell back to CPU processing. */
MMOD_API int32_t mmod_session_get_processing_status(MmodSession* session, int32_t* out_effects_enabled, int32_t* out_using_cpu_fallback);

/* One-shot: decode input video via Media Foundation, mosample, encode MP4. */
MMOD_API int32_t mmod_process_video_file(
    const wchar_t* input_path,
    const MmodSessionDesc* desc,
    MmodProgressFn progress,
    void* progress_user,
    int32_t* out_error);

/* Lossless MP4/H.264 stream copy. Input paths are separated by |. */
MMOD_API int32_t mmod_concat_video_files(
    const wchar_t* input_paths,
    const wchar_t* output_path,
    int32_t* out_error);

MMOD_API const char* mmod_error_string(int32_t error);

#ifdef __cplusplus
}
#endif
