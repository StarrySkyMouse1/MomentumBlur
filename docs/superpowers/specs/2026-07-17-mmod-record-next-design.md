# mmod_record_next 设计说明

**日期：** 2026-07-17  
**状态：** 已确认（非核心项按推荐锁定）

## 1. 目标

统一 **OBS 离线合成** 与 **游戏 `startmovie` TGA 流式合成** 为单一 WPF 应用；运动模糊在自研 C++ 中以 SVR 思路（GPU mosample + 曝光权重）完成，并经 **D3D + NVENC/AMF** 直接写出 60fps 成片。

## 2. 已确认决策

| 项 | 决策 |
|----|------|
| 应用形态 | 单 App + 模式切换 |
| 技术栈 | WPF/.NET 壳 + 自研 C++ DLL（进程内 Session） |
| 游戏输入 | 继续 `startmovie` TGA，不注入游戏 |
| 管线 | **单次流式成片**；两段落盘仅作 TGA 高压降级 |
| 编解码 | D3D11 GPU mosample（CPU 回退）+ Media Foundation H.264（优先硬件 MFT）；主路径无 `ffmpeg.exe`；**不做音轨** |
| SVR | 工作区根目录 `reference/SourceDemoRender` 只读对照，**不参与编译**；算法对齐、代码自研，日后可移除 |
| 旧项目 | 已迁入 `MomentumBlur` 并换合成后端；旧目录已移除 |

## 3. 目录与模块

```text
mmod_record/                    # 工作区根目录
  docs/                         # 设计/计划文档
  reference/
    SourceDemoRender/           # 只读对照（上游克隆）
  MomentumBlur/                 # 工具工程（唯一实现）
    src/Mmod.App                # WPF UI
    src/Mmod.Core               # 编排、设置、TGA 监视、OBS 队列、P/Invoke
    src/Mmod.Native             # 自研 C++ DLL
```

| 模块 | 职责 |
|------|------|
| Mmod.App | 导航、模式切换、设置/合成页 |
| Mmod.Core | `TgaPipeline` / `ObsPipeline`；设置；进度；Native 调用 |
| Mmod.Native | Session：D3D、mosample、硬编、写文件 |
| `reference/SourceDemoRender` | 开发对照，零引用、零链接 |

## 4. 双模式数据流

### 4.1 游戏 TGA（主路径）

```text
startmovie TGA → C# 稳帧监视 → Native Session.SubmitFrame(BGRA/纹理)
  → GPU 按 N 累加（SVR 风格权重/exposure）→ 满窗输出一帧
  → NVENC/AMF 写入 60fps 成片 → 成功后删 TGA
```

背压：待处理超阈值告警；跟不上时可降级写中间文件（非默认）。

### 4.2 OBS 视频

```text
批量队列 → Native Session.OpenVideo(path)
  → 解码到 GPU/可上传缓冲 → 同款 mosample → 硬编 60fps
  → 按需 mux 音轨（Media Foundation 或后续 native mux）
```

时序规则沿用 `mmod_record_obs` 的 `SynthesisTiming`（源 fps、N、host_timescale 慢放还原）。

## 5. Native C API（稳定边界）

```c
typedef struct MmodSession MmodSession;

typedef struct MmodSessionDesc {
  int width, height;
  int blend_frames;      // N（或合成用实际混合帧数）
  float exposure;
  int output_fps;        // 60
  int encoder;           // 0=auto, 1=nvenc, 2=amf
  const wchar_t* output_path;
} MmodSessionDesc;

MmodSession* mmod_session_create(const MmodSessionDesc* desc, int* out_error);
int mmod_session_submit_bgra(MmodSession*, const uint8_t* bgra, int stride);
int mmod_session_finish(MmodSession*);      // flush encoder
void mmod_session_destroy(MmodSession*);
int mmod_session_get_progress(MmodSession*, int* out_done, int* out_total);
```

首版允许 CPU 上传 BGRA；后续演进共享纹理/零拷贝，不改破坏性语义则扩 API。

## 6. 运动模糊模型（对齐 SVR 思路）

- 每输出帧混合 `blend_frames` 个输入帧。
- 权重由 `exposure` 生成（高斯形并归一化，行为对齐现有工具与 SVR mosample 直觉）。
- 累加在 GPU（float 累加器 → 打包输出），避免 FFmpeg `tmix`。

## 7. 错误处理与稳定性

- Native 返回错误码；C# 映射为可读故障状态。
- TGA：半写等待、跳号策略沿用旧项目经验。
- GPU/编码器不可用：明确报错；可选后续软件回退（非 v1 必做）。
- 日志：App 目录 `logs/` + Native 可选 `native_log`。

## 8. 非目标（v1）

- 不基于 SVR 源码修改或链接。
- 不注入 Momentum / Source。
- 不以 `ffmpeg.exe` 管道为合成主路径。
- **不处理音轨 / 不做音视频 mux**（只输出画面成片）。
- 不一次交付完整零拷贝纹理共享（可迭代）。

## 9. 迁移来源

| 来源 | 迁入 |
|------|------|
| `mmod_record` | TGA 监视、CFG/junction（若保留）、录制状态文案 |
| `mmod_record_obs` | 批量队列、并行、慢放指令、`SynthesisTiming` |
| 两者 | 设置模型字段合并；去掉 ComputeSharp / FFmpeg 合成主路径 |

## 10. 验证策略

Review-first：每批实现后对照本文档做代码审阅与手工冒烟（TGA 短序列 / OBS 短视频），不为每批强制新写测试套件，除非后续单独批准。
