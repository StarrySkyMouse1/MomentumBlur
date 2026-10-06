# TGA 速率与停止边界

## 当前行为（2026-10-02）

按用户要求移除动态积压控制。`fps_max` 只在每次录制开始前设置任务速率，0 仍表示不覆盖；不再预试 1 fps 排空档，不在录制中切速或回读确认。录制完成并释放管线后还原原值。删除独立速率控制器与对应超时配置，不改变超采样时间步、完整录制目标、磁盘保护或媒体校验。

最新失败为任务 `6941d161f641456e9bfa9ddb02161381` Attempt 6：完整目标 138675 帧；源帧 44780 时 Queue=102 触发切到 1 fps，约 44934 帧时查询 `fps_max` 等待确认超时。它发生在录制途中切速边界，并非正常结束边界。

另一个代码缺陷是失败/取消时内层 finally 先 Dispose 消费管线并清空引用，外层 cleanup 才请求 endmovie；外层因此无法排空现有帧，甚至可能报告 Clean。现已让 cleanup 在管线仍存活时执行，按 endmovie → 物理静默 → freeze/drain/Finish → Dispose → 环境恢复顺序处理；外层复用本次 cleanup 结果，避免重复停止。预览录制也采用相同收尾顺序，并在释放编码文件后删除失败输出。

验证只使用现有构建和 Smoke，不新增或修改测试。真实游戏完整录制和视频结尾仍需实机验证。

## 本次验证结果（2026-10-02）

- `dotnet build src/Mmod.App/Mmod.App.csproj -c Release --no-restore /p:SkipNativeBuild=true`：0 警告、0 错误；仅构建托管代码，没有修改或重建 Native。
- `dotnet run --project src/Mmod.SmokeTest/Mmod.SmokeTest.csproj -c Release --no-restore /p:SkipNativeBuild=true -- recording`：`RECORDING_OK`；测试文件没有改动。
- `git diff --check`：通过，仅有既有 LF/CRLF 提示。
- Debug 构建编译到 Core 后，复制 App 输出失败：现有 Debug 应用 PID 57304 与 Visual Studio PID 37316 占用 DLL。未关闭这两个用户进程，当前运行的应用仍是旧实现。
- 数据库只读查证；未改动任务、视频、冻结配置或历史计划，未提交或推送。
- 真实回放完整录制、立即停止及成片结尾尚未实机验收。当前 Release 应用可用于后续验证；Debug 需停止调试后重新构建。

## Attempt 7 磁盘压力调查与消费入口修复

- 任务仍为 `6941d161f641456e9bfa9ddb02161381`。2026-10-02 10:27（北京时间）在 Fed=127537、Candidate=426、Pending=0 时，6 GiB R 盘剩余 0.43 GiB，触发 10% 安全下限。停止后完成物理静默与排空，最终 Submitted=127980、Output=2133，仍少于完整目标 138675；不将总时长超过 replay 元数据误判为成功。
- 当前 R 盘无 TGA，仍有约 3.1 GiB 游戏资源占用，其中 maps 约 2.03 GiB；这不是未删除的 TGA。未删除、迁移游戏资源或修改 RAM 盘。
- 独立 ffprobe 全帧读取验证 `node_001/attempt_7_056941.partial.mp4`：H.264、1920x1080、60 fps、2133 帧、35.549983 秒、220781903 字节。保留原文件；资源管理器已打开该 node 目录。
- 消费入口去除重复大小/时间戳观察、40/120 ms 空闲等待与重复开文件验头；改为取得排斥写入者的读取句柄并确认完整 TGA 载荷后接受。仍在写入的文件、半截文件不接受，帧号隔离与有序提交保持。
- 已消费文件删除失败原本被吞掉；现明确传播包含路径的 IO 错误，不能继续静默占用磁盘。
- 磁盘保护时记录候选字节、已完成且关闭写句柄的数量、未完整或仍在写入的数量。旧日志没有这些细节，所以不能声称已经证明 426 个候选的具体阻塞原因。
- 本次仍无新的真实长录制验收；固定生成速率、磁盘安全线、完整包络及 partial 合并限制保持原契约。测试源文件未改动。

## 下次重试的日志链路（2026-10-02）

本次用户已确认 Attempt 7 视频未包含结尾。当前只能确认采集被 DiskPressure 在目标之前中断；源 MMTV v1 的 header runtime=33.9899992402643、ticks=2401 与任务数据库一致，没有源回放损坏的正向证据，不按总 MP4 时长推断播放已结束。

### 持久化与关联

- 每次 Attempt 创建同目录 `attempt_<次数>_<session前6位>.capture.log`，UTC 时间戳、自动刷新，并保留到下次重试后。不会删除原 partial 或修改历史记录。
- 开头记录 Task/Node/Attempt/Session/Prefix、日志/临时输出/正式输出路径、Core MVID 与实际程序集位置，核对是否运行最新构建。
- 记录回放路径、文件大小/修改时间/SHA256、版本/时长/ticks/阶段和完整任务冻结配置。
- 原有 Log/关键 Phase 同时写文本；周期和边界快照通过既有 Phase 进入 SQLite。命令读取期间返回的 NetCon 控制台行只写 Attempt 文本，不新增后台 NetCon 读取或任何游戏查询。
- 文本写入失败会明确报告到原有数据库日志，保留原先数据库链路；临时日志仍与 Attempt 生命周期一起关闭。

### 下一次如何区分阻塞层

每 5 秒记录 `CaptureDiagnostics`、`CaptureCandidateDiagnostics`、`PipelineDiagnostics`；开始、磁盘 Critical、endmovie 请求/确认、静默、排空、Finalize、失败/取消也记录边界快照。字段包含观察到的最大 TGA 序号、下一待提交序号、最后稳定序号、生成/消费/输出计数及吞吐、pending 字节、候选字节、磁盘 total/free/safety、解码平均耗时、Native 提交平均耗时、已删除源帧数、编码后端及 Finish 状态。

候选原因区分 Ready、WriterOrLockOpen、ShortHeader、IncompletePayload、UnsupportedHeader、UnexpectedLength、Missing、ReadError。汇总计数及最小 4 个序号的文件名、长度/预期长度、宽高/bpp/type、时间戳年龄、HRESULT；不依赖错误文本猜原因。文件系统事件溢出/错误、扫描失败及最后错误另有计数。诊断读取不改变帧所有权、编号或成功条件；普通候选检查不构造详细诊断字符串。

- MaxObserved 停止增长且没有候选：偏向生成/游戏端。
- 候选持续增长并大量 WriterOrLockOpen/IncompletePayload：写入完成边界。
- Ready 持续存在却不能提交，或下一帧缺失：watcher/有序消费路径。
- DecodeAvgMs/SubmitAvgMs 增长，产出快于消费：解码/Native 处理路径；submit 包含 GPU 与编码阻塞，不能单凭它继续细分。
- Deleted 不能跟上 submitted 或出现释放失败：删除/空间回收路径。
- endmovie 后观察序号继续增长、无法静默，或 Finish/媒体校验失败：对应收尾边界。

以上是下次日志的判读方向，不是对本次 426 张候选具体原因的既成结论。每 5 秒诊断和关键边界写日志；不记录每帧文件、不动态改速、不调整安全线或目标包络、不新增/修改测试。下一次用户显式开始/继续才录制。

### 本轮验证边界

- 最终 Release 托管构建：0 警告、0 错误，使用 `/p:SkipNativeBuild=true`，Native 未改动。
- 现有 recording Smoke 首次出现 `M3 zero delta must yield 0 rate`；该独立 RateWindow 检查连续采样 Stopwatch 时间，零间隔实现返回 null。未修改该实现或测试；最终使用已构建产物复跑返回 `RECORDING_OK`。保留首次失败结果，不将其隐去。
- `git diff --check` 通过，SmokeTest 源文件未改动。本次没有实际发起录制或改动任务数据库与视频。
- 当前应用 PID 38432 从 Debug 输出运行，尚未加载本轮日志改动。下次先关闭旧应用，打开新 Release，确认 `AttemptDiagnosticsBegin` 和对应 capture.log；日志文件与真实游戏链路仍待下次用户重试验收。

## 以下为已被取代的动态积压控制历史

## 2026-10-01 实机事实

任务 `6941d161f641456e9bfa9ddb02161381` 的阶段 4 在源帧 124856 时发生 DiskPressure；当时 Candidate=27、Pending=377，R 盘剩余 9.5%。完整目标是 138675 帧，尾帧收集从 129675 帧开始，因此保护停止发生在正常收尾边界之前。

`endmovie` 已确认，物理静默、排空、Native Finish 与 partial 媒体验证均成功；最终 125280 个输入帧生成 2088 个输出帧。排空后 R 盘没有 TGA，但 6 GiB 容量仍占用约 3.08 GiB。当前只有固定的 `fps_max=40`，没有抑制长时间积压的运行时控制。上一轮移除了静止提前结束和按总时长误判完成，本次保留这些修复。

## 修复边界

- 只在已配置前台生成速率且能可靠读取、恢复 ConVar 的自动录制中启用控制；设置为 0 仍表示不覆盖速率。
- `CaptureConVarScope` 在写命令前登记原值。录制前确认 `sv_cheats=1`、排空档 `fps_max=1` 和任务工作速率均实际生效；每次切换均独立查询回读，ACK 本身不能证明值生效。无法确认时明确失败。
- 以 Candidate + Pending 控制积压。高水位最多 128 帧，若可用空间较小，再按录制前“可用字节减安全保留字节”的四分之一及实际 TGA 平均大小收紧；低水位是高水位的四分之一。水位不随当前减速档自缩。
- 高水位或可用余量消耗一半时降到排空档。队列回到低水位、可用余量恢复到初始余量的四分之三并持续稳定 1 秒后，恢复任务工作速率。不能采样磁盘时不据此认定空间已恢复。
- 控制命令作为被主循环跟踪的任务执行，主循环持续检测取消、pipeline fault、游戏退出和磁盘 Critical，不阻塞等待 NetCon。停止录制或故障退出前取消并等待控制任务结束，再由原有流程还原 ConVar。
- 每次排空最长 2 分钟；记录请求、确认、周期进展、恢复和超时的队列、空间、实际吞吐及源帧。超时明确失败，不降低完整目标、不跳帧。
- 不调用 `endmovie` 来排空，不改变 `host_framerate`、曝光、超采样、Native 提交顺序或任务冻结设置。Critical 仍执行受控停止并保留经过验证的 partial；partial 不提升为完整片段。

## 验证边界

只运行现有构建与 Smoke，不新增或修改测试、断言或故障注入。需要真实录制日志证明 `BackpressureEnterDrain → BackpressureDrainProgress → BackpressureExitDrain → CaptureEnvelopeReached`，并检查成片结尾，才能确认实机修复通过。构建与现有 Smoke 不覆盖新增控制在真实引擎中的完整行为。

## 本轮验证记录

- Release 与 Debug 应用构建均为 0 警告、0 错误，使用 `--no-restore /p:SkipNativeBuild=true`；只验证托管改动，未重建 Native。
- 最终代码运行现有 `recording` Smoke 返回 `RECORDING_OK`；测试源文件没有修改。
- 复审修复：Critical 已触发时，速率控制任务终止过程的非取消错误记为次级错误，继续严格停止、排空与 partial 验证；正常完成路径仍传播控制错误。
- 当前未执行真实游戏的减速、恢复循环，也未重新录制并验收视频结尾；不将构建和 Smoke 结果当作这两项的通过证据。
- 本轮未修改任务数据库、已有视频或游戏安装，未提交或推送代码。

## 失败展示与停止策略（2026-10-01）

实机 Attempt 2、3 均在减速请求后回读 `fps_max` 超时，随后旧策略按 NetConLost 自动重启并重录。恢复 `sv_minupdaterate` 失败是次级清理错误，不能作为首个原因展示。

按用户要求，节点执行器移除自动重试循环；一次显式开始/继续仅创建一个 Attempt，失败执行受控清理后暂停队列。录制异常在环境恢复前报告，首个错误在清理期间保持可见，完整诊断与次级错误仍写日志。通信自身超时明确说明等待时限及非用户取消。用户手动继续仍创建新的独立 Attempt。

本次只修复失败停止与原因展示，尚未证明真实引擎中 fps_max 回读超时的底层原因或解决该通信故障。
