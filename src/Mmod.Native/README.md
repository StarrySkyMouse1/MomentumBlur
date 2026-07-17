# Mmod.Native

自研运动模糊 / 编码 DLL。首版为 **CPU 加权累加桩**，用于打通 C# 管线；后续替换为 D3D mosample + NVENC/AMF。

## 构建（Visual Studio 自带 CMake）

在「x64 Native Tools」或普通终端中：

```bat
cd src\Mmod.Native
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

若生成器名不同，可用 `cmake -G` 列出。产物：`build\bin\Release\mmod_native.dll`。

`Mmod.App` 构建后若该路径存在，会自动复制到输出目录。
