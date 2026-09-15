# 构建与发布

本文面向希望从源码编译或制作便携版的维护者。普通用户请直接下载 GitHub Release 中的 ZIP。

## 环境要求

- Windows 10/11 x64
- PowerShell 5.1 或 PowerShell 7
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Node.js 18 或更高版本（推荐使用当前 LTS）
- 首次还原 NuGet 与 npm 依赖时需要联网

Microsoft Edge WebView2 Runtime 不是编译所必需的，但运行“音频转 MIDI”界面时需要。大多数现代 Windows 10/11 环境已随 Edge 安装。

## 一键制作便携版

在仓库根目录打开 PowerShell：

```powershell
.\build-release.ps1
```

脚本会依次：

1. 使用锁文件还原前端依赖；
2. 生成本地 Basic Pitch 页面、JavaScript 包和模型资源；
3. 编译并运行内置自检；
4. 发布自包含的 Windows x64 程序；
5. 创建 `App` / `Songs` 便携目录并复制许可证；
6. 按项目版本号输出 `outputs\三角洲口风琴播放器-v<版本>-便携版.zip`，并生成 `SHA256SUMS.txt`供校验。

如果 `node_modules` 已严格按照当前 `package-lock.json` 还原，可跳过 npm 还原：

```powershell
.\build-release.ps1 -SkipNpmRestore
```

`-SkipNpmRestore` 只适合可信且完整的本地依赖目录；CI 和正式发布建议始终使用默认行为。

## 手动构建

先生成随程序发布的离线扒谱资源：

```powershell
Push-Location web\audio-transcriber
npm ci
npm run build
Pop-Location
```

`npm run build` 会把生成文件写入 `app\DeltaHarmonicaPlayer\AudioTranscriber`。该目录是构建产物，不提交到 Git。

然后还原并编译桌面程序：

```powershell
dotnet restore app\DeltaHarmonicaPlayer\DeltaHarmonicaPlayer.csproj -r win-x64
dotnet build app\DeltaHarmonicaPlayer\DeltaHarmonicaPlayer.csproj -c Release -r win-x64 --no-restore
```

不触发管理员权限提示地运行内置自检：

```powershell
dotnet exec app\DeltaHarmonicaPlayer\bin\Release\net10.0-windows\win-x64\DeltaHarmonicaPlayer.dll --self-test
```

成功时会输出 `SELF_TEST_OK`。自检覆盖键位映射、低八度、简谱解析和写入、AI 扒谱 MIDI 的双轨生成，以及停止响应。

发布自包含的单文件主程序：

```powershell
dotnet publish app\DeltaHarmonicaPlayer\DeltaHarmonicaPlayer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o out\publish
```

注意：`AudioTranscriber` 被刻意排除在单文件 EXE 之外，发布后必须与 EXE 一起保留。不要只分发 `DeltaHarmonicaPlayer.exe`。

## 发布前检查

- 在一台未安装 .NET SDK 的 Windows x64 电脑上解压并启动便携版。
- 确认首次启动出现管理员权限提示。
- 确认 `F9`、`F10` 注册成功；若失败，先排除其他程序占用。
- 用短 MIDI 检查自然音、升半音、升降八度和紧急停止。
- 用短 WAV/MP3 检查 WebView2 页面、离线模型加载和 `AI Melody` / `AI All Notes` 两条轨道。
- 断网后再做一次音频转换，确认没有误依赖在线模型。
- 检查 ZIP 中存在 `README.md`、`LICENSE`、`THIRD-PARTY-NOTICES.txt`。
- 自包含发布若找到 SDK 随附的声明，还应包含 `DOTNET-THIRD-PARTY-NOTICES.txt`。
- 不要把未经授权的歌曲、录音或 MIDI 放进公开 Release。

## 版本锁定与依赖声明

- .NET 依赖版本锁定在 `app\DeltaHarmonicaPlayer\DeltaHarmonicaPlayer.csproj`。
- 前端依赖版本及完整依赖树锁定在 `web\audio-transcriber\package-lock.json`。
- 更新依赖后应重新生成离线资源、运行自检，并同步更新 `THIRD-PARTY-NOTICES.txt`。
