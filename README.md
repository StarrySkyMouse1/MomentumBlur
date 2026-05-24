# MomentumBlur

MomentumBlur is a Windows WPF tool for offline 60fps motion-blur synthesis from high-framerate OBS recordings.

## Download

For normal use, download the ready-to-run ZIP from GitHub Releases. The Release ZIP includes the bundled FFmpeg binaries required by the GPU/OpenCL backend.

## Build from source

This public source tree does not include FFmpeg binaries because they exceed GitHub's regular file size limit. To run from source, install/download FFmpeg 8.1.1 full build and place `ffmpeg.exe` and `ffprobe.exe` under:

```text
MomentumBlur/Resources/ffmpeg/ffmpeg-8.1.1-full_build/bin/
```

Then build/test with:

```powershell
dotnet test MomentumBlur.slnx
```
