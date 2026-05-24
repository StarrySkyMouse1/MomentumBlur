# MomentumBlur

MomentumBlur 是一个面向 Momentum / Source 游戏录制素材的 Windows 运动模糊合成工具。

它会读取高帧率 OBS 录制视频，根据“OBS 录制帧率 + 超采样倍率 N”的配置做多帧加权混合，最终输出 60fps 成片。

## 下载使用

普通用户建议直接到 GitHub Releases 下载已经打包好的 ZIP：

```text
MomentumBlur-win-x64.zip
```

下载后解压，运行：

```text
MomentumBlur.exe
```

Release ZIP 会包含运行所需的 FFmpeg 文件，通常不需要额外配置。

## 基本流程

1. 在 OBS 中按设置页提示配置录制帧率。
2. 在 Momentum / Source 控制台执行软件生成的慢放指令。
3. 使用 OBS 录制游戏片段。
4. 录制结束后执行恢复指令。
5. 把录制视频添加到 MomentumBlur，点击合成。

## 推荐设置

- 帧混合后端：`GPU Resident - OpenCL/NVENC`
- NVIDIA 显卡编码器：`H.264 - NVIDIA NVENC` 或 `HEVC - NVIDIA NVENC`
- CPU 兜底后端：`FFmpeg tmix - CPU`
- 输出帧率：固定为 60fps

## 从源码运行

这个公开源码仓库不直接包含 FFmpeg 二进制文件，因为完整 FFmpeg 构建体积较大，不适合直接提交到 GitHub 普通仓库。

如果你要从源码运行，请下载 FFmpeg full build，并把文件放到：

```text
MomentumBlur/Resources/ffmpeg/ffmpeg-8.1.1-full_build/bin/ffmpeg.exe
MomentumBlur/Resources/ffmpeg/ffmpeg-8.1.1-full_build/bin/ffprobe.exe
```

然后在 `MomentumBlur` 目录中运行：

```powershell
dotnet build MomentumBlur.csproj
dotnet run --project MomentumBlur.csproj
```

## 说明

公开仓库根目录保留这个 `README.md`，让 GitHub 仓库首页可以直接显示项目说明。源码主体仍然放在 `MomentumBlur/` 文件夹中；开发文档、私有工程历史和本地打包脚本不会发布到这里。
