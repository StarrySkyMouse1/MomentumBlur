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

在 VS 里按 F5 / 生成 `Mmod.App` 时，会**自动**调用 cmake 增量编译 `mmod_native` 并复制 dll 到输出目录。仅改 C#、想跳过 Native 时可加：`/p:SkipNativeBuild=true`。

## 功能清单（交测）

| 能力 | 说明 |
|------|------|
| TGA 流式 | 监视 → Native GPU 混合 → MP4 |
| OBS 批量 | 拖放/多选，可并行，慢放指令可复制 |
| CFG | 设置页创建 Junction 时一并生成 `mmod_record.cfg`，可复制 `exec mmod_record` |
| Junction | momentum ↔ RAM 盘 |
| ImDisk | 设置页一键打开内置 RamDiskUI |
| 编码 | D3D11 mosample + MF H.264（硬件 MFT 优先） |

冒烟：`dotnet run --project src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release`