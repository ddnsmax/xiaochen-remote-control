# v1.7 原生视频组件构建记录

## 固定来源

- H264Sharp：提交 `b9a97bf9d51191f083e7b992342a848789d59728`，对应 NuGet `H264Sharp 1.6.0` 的原生接口。
- libyuv：提交 `53e014c99d6f59647c57b70b3fa65ad3dd59ce08`，与 `Lennox.LibYuvSharp 1.1.2` 使用的 ABI 对齐。
- OpenH264：NuGet `H264Sharp 1.6.0` 附带的 `openh264-2.4.1-win64.dll`。
- 工具链：`llvm-mingw 20260616 stable ucrt x86_64`、CMake 4.4.0、Ninja 1.13。
- llvm-mingw 压缩包 SHA256：`B9B68A4D276E16FA25802AABA458E4638F64B3884C290AACCDC2D87083B6CA35`。

## 构建 H264SharpNative

将 llvm-mingw 的 `bin` 目录加入 `PATH`，然后执行：

```powershell
cmake -S .\ThirdParty\H264SharpNative-static `
      -B .\build\h264sharp-static `
      -G Ninja `
      -DCMAKE_BUILD_TYPE=Release `
      -DCMAKE_C_COMPILER=clang `
      -DCMAKE_CXX_COMPILER=clang++
cmake --build .\build\h264sharp-static --config Release
```

CMake 对 MinGW/LLVM-MinGW 使用 `-static -Wl,--gc-sections`，输出 `H264SharpNative-win64.dll`。

## 构建 libyuv

```powershell
cmake -S .\ThirdParty\libyuv-static `
      -B .\build\libyuv-static `
      -G Ninja `
      -DCMAKE_BUILD_TYPE=Release `
      -DCMAKE_C_COMPILER=clang `
      -DCMAKE_CXX_COMPILER=clang++ `
      -DTEST=OFF
cmake --build .\build\libyuv-static --config Release --target yuv_shared
```

输出 `libyuv_internal.dll`。共享库定义 `LIBYUV_BUILDING_SHARED_LIBRARY`，并静态链接 C++ 运行库。

## ABI 与依赖验证

使用 `llvm-objdump -p <dll>` 检查 `DLL Name`。禁止出现：

- `MSVCP140.dll`
- `VCRUNTIME140.dll`
- `CONCRT140.dll`
- `libc++.dll`
- `libunwind.dll`
- `libgcc_s_*.dll`
- `libstdc++-6.dll`
- `libwinpthread-1.dll`

发布前还必须完成：

1. 对比旧版与新版导出表。
2. 对三个原生 DLL 执行 LoadLibrary。
3. 运行 `dotnet test` 全量测试。
4. 用最终发布的单文件 B端.exe 再运行一遍全量测试。

## 已验证成品哈希

| 文件 | SHA256 |
|---|---|
| H264SharpNative-win64.dll | `BC9EFA6BFBAF7F37841A906FACDCAD6D835F7A6EFE63B90DA2C4DA7873967839` |
| libyuv_internal.dll | `C54FFCE18F1A8A6E8F5103AEA7BAC3DAB2DE3D71C783A6FD1E9AD8508D955F53` |
| openh264-2.4.1-win64.dll | `081B0C081480D177CBFDDFBC90B1613640E702F875897B30D8DE195CDE73DD34` |
