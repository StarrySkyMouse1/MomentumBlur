# recording-chain-fault-injection.ps1 — Windows 实机人工故障注入（可选）
#
# 目的：故意制造外部故障，验证状态机 fail-safe（不是“尽量继续”）：
#   - 录制中关闭 Momentum
#   - 录制中断开/阻断 NetCon
#   - 录制中把 watch directory 变只读
#   - endmovie 前后点 StopNow
#   - replay watch 前后点 StopNow
#   - final TGA 正在写时点 StopNow
#   - App 进程强制结束后重新启动（验证 crash recovery）
#
# 用法：
#   pwsh scripts/recording-chain-fault-injection.ps1 -AppExe "D:\...\mmod_record_next.exe" -WatchDir "R:\momentum"
#
# 每个场景后人工检查：
#   1. 节点未误标 Completed（除非真的录完整了）
#   2. 没有 orphan Momentum 进程（任务管理器）
#   3. 没有继续写盘的 startmovie
#   4. 下次启动能恢复 Pending/Paused 并清理旧 prefix TGA

param(
    [Parameter(Mandatory = $true)][string]$AppExe,
    [string]$WatchDir = "R:\momentum"
)

$ErrorActionPreference = "Stop"

function Assert-NoOrphan([string]$label) {
    $p = Get-Process -Name "momentum" -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host "✗ [$label] 检测到遗留 momentum 进程 PID=$($p.Id -join ',')" -ForegroundColor Red
    } else {
        Write-Host "✓ [$label] 无遗留 momentum 进程" -ForegroundColor Green
    }
}

function Start-App { Start-Process -FilePath $AppExe -PassThru }

Write-Host "==== 故障注入清单（每项手动/半自动执行） ===="

# 场景 1：录制中关闭 Momentum（在任务 Running 时结束 momentum.exe 进程树）
Write-Host "`n[场景 1] 录制中关闭 Momentum："
Write-Host "  1. 在应用中创建并启动 1 节点任务"
Write-Host "  2. 等任务进入 Running，用任务管理器结束 momentum.exe"
Write-Host "  3. 期望：节点秒级 Failed（GameExited），不等待 2 分钟"
Assert-NoOrphan "场景 1 之后"

# 场景 2：录制中断开 NetCon
Write-Host "`n[场景 2] 录制中断开 NetCon："
Write-Host "  1. 用 tcpview/netsh 阻断 netcon 端口（或结束游戏内 NetCon 会话）"
Write-Host "  2. 期望：endmovie 无法确认 → CaptureStopUnconfirmed → 游戏会话重建后重试"

# 场景 3：watch directory 变只读
Write-Host "`n[场景 3] watch directory 变只读："
Write-Host "  1. 录制中执行：icacls `"$WatchDir`" /deny *S-1-1-0:(W)"
Write-Host "  2. 期望：受控停止或 DiskPressure 失败，不写坏"
Write-Host "  恢复：icacls `"$WatchDir`" /remove:d *S-1-1-0"

# 场景 4：endmovie 前后点 StopNow
Write-Host "`n[场景 4] endmovie 前后点 StopNow："
Write-Host "  1. 录制接近 SafeEnd 时点「立即停止」"
Write-Host "  2. 期望：独立 cleanup token 完成 endmovie → quiescence → pipeline finalize → 游戏 quit"

# 场景 5：replay watch 前后点 StopNow
Write-Host "`n[场景 5] replay watch 前后点 StopNow："
Write-Host "  1. 在 startmovie 之后、watch 之前点 StopNow"
Write-Host "  2. 期望：startmovie 已停止，无继续写盘 TGA"

# 场景 6：final TGA 正在写时点 StopNow
Write-Host "`n[场景 6] final TGA 正在写时点 StopNow："
Write-Host "  1. 在 SafeEnd 刚到时立刻点 StopNow"
Write-Host "  2. 期望：watcher quiescence 收齐最后一帧或明确失败，不丢帧不误报成功"

# 场景 7：App 强杀后重启（crash recovery）
Write-Host "`n[场景 7] App 强杀后重启："
Write-Host "  1. 录制中 Stop-Process -Force $AppExe"
Write-Host "  2. 重新启动应用并「开始/继续」"
Write-Host "  3. 期望：runner_session 恢复 → 停止遗留 momentum（身份校验后）→ 清理旧 prefix TGA → 节点回 Pending"

Write-Host "`n==== 验收原则 ===="
Write-Host "任何外部错误都不能被误判成成功；任何不确定状态都不能未经恢复就传递到下一阶段。"
