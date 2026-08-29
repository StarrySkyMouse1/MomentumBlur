# reference

工作区根目录下的**只读参考**资料，不参与 `MomentumBlur` 编译与链接。

## SourceDemoRender

路径：`reference/SourceDemoRender`

- 来源：[crashfort/SourceDemoRender](https://github.com/crashfort/SourceDemoRender)
- 用途：对照运动模糊（mosample）、曝光/采样与 D3D 相关实现思路
- 约束：不修改其构建产物；不作为 submodule 链入 `MomentumBlur`；自研 C++ 在 `MomentumBlur/src/Mmod.Native`

更新参考树（可选）：

```bat
cd reference\SourceDemoRender
git pull
```
