# mmod_record_next Implementation Plan

> review-first；音轨不做。

**Goal:** 双模式可试用，画面运动模糊成片；旧版核心能力迁齐。

**Architecture:** Mmod.App → Mmod.Core → Mmod.Native（D3D11 mosample + MF H.264 硬编 MFT）

## Global Constraints

- 无 `ffmpeg.exe` 主路径；音轨不做
- SVR 仅 `reference/SourceDemoRender` 只读
- 单次流式成片

---

### 已完成

- [x] 工程骨架 / 文档 / reference SVR
- [x] Native Session + `mmod_process_video_file`
- [x] **D3D11 GPU mosample**（失败回退 CPU）
- [x] MF H.264 + 硬件 transform 优先
- [x] TGA 监视管线 / OBS 批量（含并行）
- [x] CFG 生成 / Junction / OBS 慢放指令复制
- [x] 设置页完整字段
- [x] SmokeTest 流式 + OBS

### 明确不做

- 音轨 mux
- 基于 SVR 改源码 / 注入游戏
- 显式第三方 NVENC/AMF SDK 头文件集成（由 MF 硬件 MFT 承担；设置项保留偏好）

---

**交测状态：** 可交给用户做 TGA/OBS 实机测试。
