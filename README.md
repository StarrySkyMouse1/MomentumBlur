# MomentumBlur

统一版 Momentum 运动模糊合成（OBS + TGA）。只处理画面，不做音轨。

本仓库是唯一工具工程（旧 mmod_record / mmod_record_next 已退役）。设计文档与参考源码在上级工作区：`../docs`、`../reference/SourceDemoRender`。

## 运行

原生库 `mmod_native` 用 **CMake + VS C++** 构建（不在纯 C# 工程里）。首次或换机后先配置一次：

```bat
cd C:\Projects\else\.net\WPF\mmod_record\MomentumBlur
cmake -S src\Mmod.Native -B src\Mmod.Native\build -G "Visual Studio 18 2026" -A x64
cmake --build src\Mmod.Native\build --config Release
dotnet run --project src\Mmod.App\Mmod.App.csproj -c Release
```

解决方案：`MomentumBlur.slnx`（含 C# + `mmod_native`）。若解决方案里看不到 C++ 项目，说明还没跑过上面的 `cmake -S ...`（会生成 `src\Mmod.Native\build\mmod_native.vcxproj`），配置后再重新打开解决方案即可。

在 VS 里按 F5 / 生成 `Mmod.App` 时，会从**当前 Visual Studio 实例的安装目录**自动发现其内置 CMake，增量编译 `mmod_native` 并复制 dll 到输出目录；因此支持默认目录、自定义目录以及 Preview/Insiders 版本，不依赖某台电脑的固定盘符。命令行构建会继续从标准 VS 目录或 `PATH` 查找 CMake，也可显式传入 `/p:CMakeExe=完整路径`。仅改 C#、想跳过 Native 时可加：`/p:SkipNativeBuild=true`。

## 功能清单（交测）

| 能力 | 说明 |
|------|------|
| TGA 流式 | 每个节点先确认旧录制停止并清理监视目录遗留 TGA；消费线程就绪后再 startmovie，落盘即进入 Native GPU 混合 → 画质处理（可选）→ MP4 |
| 回放兼容 | 本地与在线 MMTV v1/v2 均可扫描和执行；未知版本由解析器明确拒绝，不再把游戏可播放的在线 v1 阶段回放误判为旧版不兼容 |
| 有序消费加速 | TGA 读取/24→32 位展开使用有限并行预解码，按连续帧号单序提交；Native 使用三槽 D3D11 上传环减少 staging 重用等待，缺帧或所有权异常直接失败，不跳帧、不乱序 |
| 前台录制背压 | TGA 设置可配置现实生成速率上限（默认 90 fps，0 表示不覆盖）；自动录制期间按任务冻结值临时设置 `fps_max`，`host_framerate` 的超采样时间步保持不变，结束后恢复用户原值 |
| 稳定录制环境 | 自动录制按 Attempt 保存并临时设置 Source 渲染/插值相关 ConVar（`mat_queue_mode`、前后台节流、预测、插值与更新率），结束、失败或取消时逆序恢复；无法读取原值的可选变量不覆盖 |
| 失败任务续跑 | 待执行、已暂停或失败待处理的任务可单独修正前台 TGA 生成速率上限；已完成阶段片段保持不变，点击“开始 / 继续”后从失败及后续节点继续，其他画面与编码快照仍保持冻结 |
| 私人单人录制 | 每个新游戏会话先离开原大厅并创建仅邀请的 Steam 私人大厅，保留登录身份与头像，同时隔离公屏聊天和其他玩家；工具自启游戏时使用简体中文，复用已打开的游戏时保留该实例的语言 |
| 录制 HUD | 设置可冻结到任务；隐藏时在自动回放前执行 `cl_drawhud 0`，同时隐藏顶部回放控制条和游戏 HUD，录制结束后恢复显示 |
| OBS 批量 | 拖放/多选，可并行，慢放指令可复制 |
| CFG | 设置页创建 Junction 时一并生成 `mmod_record.cfg`，可复制 `exec mmod_record` |
| Junction | momentum ↔ RAM 盘 |
| ImDisk | 设置页一键打开内置 RamDiskUI |
| 编码 | D3D11 mosample + MF H.264（硬件 MFT 优先） |
| 画质处理 | Motion-Adaptive Detail / Micro Detail Low-Pass / Deband (No Dither) / Temporal Shimmer，全部可独立勾选 |
| 即时画质预览 | 播放器下方分为“阶段 1 · 获取底片”和“阶段 2 · 参数合成”两个页签：阶段 1 负责搜索回放、抓取并播放固定 60× 慢放底片；阶段 2 负责参数摘要、并行合成、遥测和结果播放。两个阶段独立按钮控制并共享上方播放器及可拖动播放进度条 |
| 色彩描述 | H.264 母版明确标记为 Rec.709 SDR；RGB 输入按 Full Range 解释，编码输出按 Limited Range 标记，避免播放器或后期软件误判黑白位与色彩矩阵 |
| Motion Blur | Legacy Gaussian Exposure（默认，保持旧行为）+ Shutter Angle 180°~360°（推荐） |
| KSF 风格预设 | TGA 离线录制一键应用 60× 超采样、360° 全快门、60 fps、120 Mbps，并关闭额外滤镜；参考 SVR 高采样/满曝光语义，不声称复刻 KSF 未公开的频道私有参数 |
| 中间母版码率 | 设置页按 Mbps 配置并真正传给 MF 编码器（0 = 自动，1–120 Mbps；旧快照中的 1–120 自动按 Mbps 迁移） |
| 磁盘安全 | 监视盘安全下限按 0–50% 配置；预警线为安全线 +5%；Critical 受控收尾并保留已验证 partial |
| DaVinci 指引 | 设置页可勾选并一键复制 DaVinci 4K AI 后处理步骤 |

冒烟：`dotnet run --project src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release`

## Bilibili 低码率优化工作流

```text
Source / Momentum Replay
        ↓
高时间采样输入（TGA / OBS）
        ↓
Temporal Supersampling / Motion Blur
        ↓
可组合 Video Processing Pipeline（画质处理，全部可独立开关）
        ↓
1920×1080 / 60fps 高质量中间母版（本工具输出基线，不在工具内做 AI 放大）
        ↓
DaVinci Resolve Studio：Super Scale 2x Enhanced
        ↓
3840×2160 / 60fps 高码率上传源
        ↓
Bilibili 二压（重点保证 1080p60 / 1080p30 低码率档的运动观感）
```

### 为什么避免 Film Grain / 强锐化

Bilibili 会对上传源再次转码。每一帧随机噪点 / 胶片颗粒会直接消耗低码率档的码率，
让 ramp、HUD、几何体分不到足够 bit，宏块和纹理崩坏更严重。因此：

- 本工具的 Deband **默认 No Dither**（不添加随机抖动 / 胶片颗粒）。
- 推荐 Preset「B站低码率推荐」只开启 Motion-Adaptive Detail Reduction + 轻量 Micro Detail Low-Pass。
- 在 DaVinci 中不要叠加强 NR / Film Grain / Clarity / Texture / Midtone Detail，
  避免重新制造 micro detail，抵消工具端为低码率压缩做的优化。

### 全部处理器可关闭，旧行为保留

- 老 `settings.json` / 老任务 `SettingsJson` 没有新字段时：所有新模块默认关闭，
  Motion Blur 走 Legacy Gaussian Exposure，码率走自动估算 —— 与旧版一致。
- 处理器全部关闭时，Native 走旧快速路径（Accumulate → Pack → Encode），不增加额外 GPU pass。
- 渲染任务在创建时冻结完整处理配置（MotionBlurMode / ShutterAngle / QualityModules / TargetBitrate），
  之后修改全局设置不影响已创建任务。

### 参数语义说明

- **Exposure** 不是物理快门角，而是 Legacy Gaussian 权重分布的宽度（sigma ≈ exposure × N × 0.5），语义未变。
- **Shutter Angle**（180°~360°）是新推荐模式：有效样本 ≈ N × angle / 360，居中 Box 窗口，权重归一化。
  兼顾 4K60 上传与 1080p30 观看：建议先 AB 测试 300°~360°，不硬编码唯一最佳值。

## AB 测试（可选）

`scripts/quality-ab-test.ps1` 需要系统存在 ffmpeg；没有时只提示未安装，不影响主程序。
它从 1080p60 母版生成 `1080p60 ~6 Mbps` / `1080p30 ~3 Mbps` 两个低码率代理预览，
用于肉眼比较（这只是“低码率代理预览”，不是 Bilibili 精确模拟器）。
AB 对比重点：高速 ramp 边缘、高频贴图 / 远处细线 shimmer、HUD 数字、大面积渐变色带。
判断标准是**低码率代理的运动观感**，不是本地 master 暂停截图的像素锐度。

## Recording State Machine（任务录制链路可靠性）

> **A recording attempt is successful only when success is positively proven at every irreversible boundary. Absence of an error is not proof of success.**

任务录制链路（Replay → TGA → GPU 合成 → MP4 → 合并）已从“正常情况下能跑通”升级为 Attempt-scoped、evidence-driven 状态机。核心原则：

- **每个 Attempt 独立身份**：每次尝试创建唯一 `AttemptId + CaptureSessionId + TGA 序列前缀`
  （如 `mmod_ab12_003_a1_9cd13b_`），`TgaDirectoryWatcher` 只消费当前前缀的 `{prefix}{index}.tga`，
  绝不全局消费 `*.tga`。失败清理也只删除当前前缀文件。
- **正向证据，不是“没看到错误”**：
  - 地图 Ready：`IMapReadinessProbe` 尝试从 `status` 输出正向读取当前地图；读不到时降级为
    “仅引擎响应”并显式标记 `Degraded`，绝不用 echo + 固定 sleep 冒充 MapReady。
  - Replay 开始：`VisualPlaybackEvidenceProbe` 用低分辨率 block-grid 计算 changed-block ratio +
    mean luma delta，要求连续 N 帧显著才建立 anchor（HUD 微变、单帧噪点不会触发）。
  - 回放结束：已经建立 PlaybackEvidence 后，连续 1 秒画面没有 block 级变化即建立 ReplayEndEvidence；预计时长仅作为最大上限兜底。
  - 停止：建立 ReplayEndEvidence 或达到预计时长上限后，`endmovie` 走 strict 命令（ACK + 失败 pattern），随后用 watcher 的**物理静默**
    （无新写入 + 候选清空 + 最终全量扫描后保持安静）证明写盘真的停止。
- **Fault 必须传播**：pipeline 后台异常、Native Finish 失败、encoder flush 失败一律 throw，
  禁止 `catch {}` 后仅凭 `File.Exists && Length > 0` 判定成功。
- **尾帧竞态修复**：严格顺序 = strict endmovie → watcher 继续运行 → 物理静默 → 最终扫描 →
  freeze → 确定性顺序 drain → 断言 candidate=0/pending=0 → Native Finish → 媒体校验 → 原子提交。
- **统一 Cleanup Barrier**：所有失败/取消路径走 `CaptureCleanupCoordinator`，使用**独立 bounded
  cleanup token**（用户取消令牌不会取消清理）；cleanup 无法证明干净时游戏会话判 Dirty，禁止
  same-session retry，必须重建游戏会话。
- **Retry 分类**：`RecordingFailureKind` 决定重试策略（SameSession / ReloadMap / RestartGame）；
  永久性输入错误不浪费重试；`CaptureStopUnconfirmed / TgaQuiescenceTimeout / NetConLost / GameExited`
  强制重启游戏。
- **原子输出**：每个 Attempt 写独立临时文件 → MediaProbe 校验（容器、分辨率、fps、时长 vs 实际
  输出帧数交叉验证）→ fsync → 原子移动到正式 ClipPath → 数据库 Completed。
- **崩溃恢复**：`runner_session` 持久化 owned 进程身份（PID + exe + start time + session prefix）；
  应用启动时先身份校验（防 PID reuse）再停止遗留进程、按前缀清理 TGA、丢弃 partial clip、节点回 Pending。
- **健康监控**：录制循环竞争 用户取消 / pipeline fault / 游戏进程退出 / 进度超时；游戏退出秒级失败；
  磁盘空间低于安全下限进入受控停止（DiskPressure）。
- **运行诊断**：任务页以最高 4 Hz 显示监视盘百分比/GiB、安全线/预警线、吞吐、积压趋势
  和真实处理/编码后端；常驻显示当前任务冻结的容器/编码、源分辨率策略、帧率、目标码率、超采样、
  模糊方式、画质处理与输出路径；预计剩余时间按所有未完成节点的剩余输入帧与当前 10 秒滚动消费速度实时计算，
  UI 最多 4 Hz 只做内存运算，任务/节点数据库仍最多每秒读取一次。每个输出窗口还会低成本抽样源 TGA，
  在任务日志记录近静止后突跳次数；该指标只用于定位源运动节奏，不改变任务成功判定。

自动化测试（无需真实游戏）：`dotnet run --project src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release recording`
覆盖 happy path、HUD-only 不触发、场景运动连续触发、单帧噪点不触发、pipeline fault、finish fault、
endmovie timeout、游戏退出、取消、prefix 隔离、慢写尾帧、静默超时、retry 策略矩阵、媒体校验（截断/错 fps/错分辨率）。

实机脚本（可选）：`scripts/recording-chain-soak.ps1`（连续 N 节点验收）、
`scripts/recording-chain-fault-injection.ps1`（故障注入清单）。

## 已知边界

- 工具内不做 AI 放大；4K 由 DaVinci Resolve Studio 的 Super Scale 2x Enhanced 完成（Studio 功能）。
- 本工具仍只输出画面；音频（如需）在 DaVinci 中加入（48 kHz AAC，约 320 kbps）。
- 地图就绪与 replay 失败文本等真实 console 输出格式未在本环境实机验证；`IMapReadinessProbe` 已提供
  degraded fallback，`startmovie/endmovie` 失败 pattern 需实机确认后作为 hard condition。
