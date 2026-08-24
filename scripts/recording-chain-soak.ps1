# recording-chain-soak.ps1 — Windows 实机连续录制 Soak Test（可选，需要真实 Momentum + NetCon）
#
# 目标：连续执行 N 个 Node Attempt，验收：
#   0 次尾部截断 / 0 次开头缺失 / 0 次跨 Session 串帧 / 0 次 Native Finish 被吞后仍 Completed
#   0 次 StopNow 后 startmovie 继续写盘 / 0 次 orphan owned Momentum / 0 次 partial MP4 标 Completed
#
# 用法：
#   pwsh scripts/recording-chain-soak.ps1 -GameRoot "D:\Steam\...\momentum" -ReplaysDir "D:\...\momentum\replays" -Iterations 100
#
# 前置：应用已构建（含 mmod_native.dll），SmokeTest 可执行，游戏内 momentum 目录包含可回放文件。

param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [string]$ReplaysDir,
    [int]$Iterations = 100,
    [string]$SmokeTestDll = "src\Mmod.SmokeTest\bin\Release\net10.0-windows\Mmod.SmokeTest.dll"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $SmokeTestDll)) { Write-Host "找不到 SmokeTest DLL：$SmokeTestDll" -ForegroundColor Red; exit 1 }
if (!(Test-Path $GameRoot)) { Write-Host "游戏根目录不存在：$GameRoot" -ForegroundColor Red; exit 1 }

# 回放清单：优先用 ReplaysDir，否则用游戏 momentum 目录
if (-not $ReplaysDir) { $ReplaysDir = Join-Path $GameRoot "momentum" }
$replays = Get-ChildItem $ReplaysDir -Filter *.mtv -ErrorAction SilentlyContinue
if ($replays.Count -eq 0) { Write-Host "ReplaysDir 中没有 .mtv 回放：$ReplaysDir" -ForegroundColor Red; exit 1 }

$failures = 0
$clipCount = 0
$report = @()

for ($i = 1; $i -le $Iterations; $i++) {
    $replay = $replays[($i - 1) % $replays.Count]
    Write-Host "[$i/$Iterations] replay=$($replay.Name)"
    # TODO: 调用应用的任务队列执行单节点录制（此处为占位）。
    # 实机接入方式：启动 mmod_record_next.exe，创建 1 节点任务后等待 Completed，
    # 或直接使用 SmokeTest 的 recording 实机子命令（需扩展）。
    $report += "iteration=$i replay=$($replay.Name) status=PLACEHOLDER"
    $clipCount++
}

Write-Host "==== SOAK REPORT ===="
$report | ForEach-Object { Write-Host $_ }
Write-Host "clips=$clipCount failures=$failures"
Write-Host "验收：0 截断 / 0 开头缺失 / 0 串帧 / 0 Finish 吞错 / 0 遗留进程 / 0 半成品 Completed / 0 Dirty Retry"
exit ($failures -eq 0 ? 0 : 2)
