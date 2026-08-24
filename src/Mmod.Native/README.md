# Mmod.Native

自研运动模糊 / 画质处理 / 编码 DLL。包含 D3D11 Compute Shader 的时间累加（temporal accumulation）、
可组合画质处理管线（GPU 优先，CPU fallback）与 Media Foundation H.264 输出。

## 模块

| 文件 | 职责 |
|------|------|
| `src/session.cpp` | Session 生命周期、权重构建（Legacy Gaussian / Shutter Angle）、MF 编码 |
| `src/gpu_blend.cpp` | D3D11 累加 + GPU 画质处理 shader（motion-adaptive / lowpass / deband / shimmer） |
| `src/frame_processing.cpp` | CPU 画质处理参考实现（GPU 初始化失败时的 fallback，数学与 shader 一致） |
| `include/mmod_native.h` | C ABI：`MmodEffectDescV1` 效果描述数组 + `target_bitrate` + `motion_blur_mode` / `shutter_angle` |

## 处理顺序（固定逻辑阶段，不依赖 UI 排序）

```text
Temporal accumulation / Motion Blur
  → Temporal processors（Temporal Shimmer）
  → Motion-aware spatial processors（Motion-Adaptive Detail）
  → Global spatial processors（Micro Detail Low-Pass / Deband）
  → Pack BGRA
  → MF H.264 encode
```

- UI 的 Order 仅用于同 stage 内排序。
- 未知 effect type 安全忽略；已知且启用的 effect 初始化失败会回退 CPU 处理，绝不静默输出未处理视频
  （可通过 `mmod_session_get_processing_status` 查询是否回退）。
- 所有效果关闭时走旧快速路径（Accumulate → Pack → Encode），不增加额外 GPU pass。

## 构建（Visual Studio 自带 CMake）

```bat
cd src\Mmod.Native
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

若生成器名不同，可用 `cmake -G` 列出。产物：`build\bin\Release\mmod_native.dll`。

配置完成后，主解决方案 `MomentumBlur.slnx` 会加载 `build\mmod_native.vcxproj`，可在同一 VS 里改 C++ / C#。

`Mmod.App` 构建后若该路径存在，会自动复制到输出目录。
