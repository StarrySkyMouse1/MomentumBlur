# mmod_record_next

统一版 Momentum 运动模糊合成（OBS + TGA）。只处理画面，不做音轨。

## 运行

```bat
cd C:\Projects\else\.net\WPF\mmod_record\mmod_record_next
cmake -S src\Mmod.Native -B src\Mmod.Native\build -G "Visual Studio 18 2026" -A x64
cmake --build src\Mmod.Native\build --config Release
dotnet run --project src\Mmod.App\Mmod.App.csproj -c Release
```

## 功能清单（交测）

| 能力 | 说明 |
|------|------|
| TGA 流式 | 监视 → Native GPU 混合 → MP4 |
| OBS 批量 | 拖放/多选，可并行，慢放指令可复制 |
| CFG | 设置页生成 `mmod_record.cfg` |
| Junction | momentum ↔ RAM 盘 |
| 编码 | D3D11 mosample + MF H.264（硬件 MFT 优先） |

参考树：`../reference/SourceDemoRender`

冒烟：`dotnet run --project src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release`
