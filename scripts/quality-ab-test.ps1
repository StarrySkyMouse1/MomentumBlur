# quality-ab-test.ps1 — 低码率代理预览生成（可选，非应用依赖）
#
# 用途：从 1080p60 高质量中间母版生成近似 Bilibili 低码率档的代理文件，
#       用于肉眼 AB 比较。结果不叫“Bilibili 精确模拟器”——平台编码器/参数会变。
#
# 用法：
#   pwsh scripts/quality-ab-test.ps1 -InputMaster path\to\master.mp4 [-OutDir .\preview]
#
# 需要系统存在 ffmpeg（PATH 或 -Ffmpeg 参数）；没有时脚本只提示未安装，不影响主程序。

param(
    [Parameter(Mandatory = $true)][string]$InputMaster,
    [string]$OutDir = "preview",
    [string]$Ffmpeg = "ffmpeg"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $InputMaster)) {
    Write-Host "错误：找不到输入文件 $InputMaster" -ForegroundColor Red
    exit 1
}

$ffmpegExe = Get-Command $Ffmpeg -ErrorAction SilentlyContinue
if ($null -eq $ffmpegExe) {
    Write-Host "ffmpeg 未安装（或在 PATH 中找不到）。本脚本为可选开发脚本；主程序不依赖 ffmpeg。" -ForegroundColor Yellow
    exit 2
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$name = [System.IO.Path]::GetFileNameWithoutExtension($InputMaster)
$p60 = Join-Path $OutDir "$name`_preview_1080p60_6m.mp4"
$p30 = Join-Path $OutDir "$name`_preview_1080p30_3m.mp4"

Write-Host "生成低码率代理预览（仅用于肉眼 AB 比较，不精确模拟 Bilibili）："
Write-Host "  $p60"
Write-Host "  $p30"

& $ffmpegExe.Source -y -i $InputMaster `
    -vf "scale=1920:1080:flags=lanczos,fps=60" `
    -c:v libx264 -preset slow -crf 23 -maxrate 6M -bufsize 12M `
    -pix_fmt yuv420p -movflags +faststart $p60
if ($LASTEXITCODE -ne 0) { Write-Host "生成 1080p60 代理失败" -ForegroundColor Red; exit 3 }

& $ffmpegExe.Source -y -i $InputMaster `
    -vf "scale=1920:1080:flags=lanczos,fps=30" `
    -c:v libx264 -preset slow -crf 24 -maxrate 3M -bufsize 6M `
    -pix_fmt yuv420p -movflags +faststart $p30
if ($LASTEXITCODE -ne 0) { Write-Host "生成 1080p30 代理失败" -ForegroundColor Red; exit 4 }

Write-Host "完成。AB 对比重点：高速运动观感、HUD 清晰度、斜坡边缘、远处细线、大面积渐变色带。"
Write-Host "判断标准是低码率代理的运动观感，不是本地 master 暂停截图的像素锐度。"
