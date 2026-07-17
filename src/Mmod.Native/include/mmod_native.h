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

enum MmodError {
  MmodError_Ok = 0,
  MmodError_InvalidArg = 1,
  MmodError_OutOfMemory = 2,
  MmodError_NotImplemented = 3,
  MmodError_SubmitFailed = 4,
  MmodError_EncodeFailed = 5,
  MmodError_IoFailed = 6,
  MmodError_DecodeFailed = 7,
  MmodError_ComFailed = 8
};

typedef struct MmodSessionDesc {
  int32_t width;
  int32_t height;
  int32_t blend_frames;
  float exposure;
  int32_t output_fps;
  int32_t encoder; /* MmodEncoderKind */
  const wchar_t* output_path;
} MmodSessionDesc;

typedef void (*MmodProgressFn)(void* user, int32_t done, int32_t total);

MMOD_API MmodSession* mmod_session_create(const MmodSessionDesc* desc, int32_t* out_error);
MMOD_API int32_t mmod_session_submit_bgra(MmodSession* session, const uint8_t* bgra, int32_t stride);
MMOD_API int32_t mmod_session_finish(MmodSession* session);
MMOD_API void mmod_session_destroy(MmodSession* session);
MMOD_API int32_t mmod_session_get_progress(MmodSession* session, int32_t* out_done, int32_t* out_total);

/* One-shot: decode input video via Media Foundation, mosample, encode MP4. */
MMOD_API int32_t mmod_process_video_file(
    const wchar_t* input_path,
    const MmodSessionDesc* desc,
    MmodProgressFn progress,
    void* progress_user,
    int32_t* out_error);

MMOD_API const char* mmod_error_string(int32_t error);

#ifdef __cplusplus
}
#endif
