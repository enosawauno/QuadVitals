# 开发与构建

本文面向参与 QuadVitals 开发的贡献者。用户安装说明见 [README](README.md)。

## 环境

- Windows 10 x64（22H2 / 19045）
- Visual Studio 2022 Build Tools（MSVC x64 + Windows SDK 10.0.19041）
- PowerShell 5.1
- 不需要 .NET SDK；托管代码由 Build Tools 自带的 Roslyn `csc` 编译

构建脚本默认使用本机安装位置：`csc` 在
`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`，
原生 DeskBand 用 `VC\Tools\MSVC\14.44.35207` + 安装的 Windows SDK（`native-toolchain.ps1` 自动取最新版本）。

可覆盖的环境变量：

| 变量 | 作用 |
|---|---|
| `QUADVITALS_VC` | MSVC 工具集根目录（版本不同时用） |
| `QUADVITALS_SDK` | Windows SDK 根目录（指向解包的 NuGet 包或自定义安装位置） |
| `QUADVITALS_LIB` | 两个运行时依赖 DLL 所在目录，默认 `lib\` |

两个依赖不随仓库分发，首次构建前放进去（缺了也能编，只是温度读不到）：

- `lib\LibreHardwareMonitorLib.dll`（0.9.2）
- `lib\HidSharp.dll`（2.1.0）

版本与许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

产品图标在 `assets\icon\` 里，随仓库分发。母版是 `png\icon-1024.png`，三个 ico 都由它生成：

```powershell
powershell -File scripts\make-app-icon.ps1 -Master assets\icon\png\icon-1024.png -Output assets\icon\app.ico        -Fill 1.0
powershell -File scripts\make-app-icon.ps1 -Master assets\icon\png\icon-1024.png -Output assets\icon\quadvitals.ico -Fill 1.0
powershell -File scripts\make-app-icon.ps1 -Master assets\icon\png\icon-1024.png -Output assets\icon\tray.ico       -Fill 1.25
```

图标为本项目原创，不含第三方图形或字体；改图后重跑上面三条即可。缺图标时构建不中断，exe 用系统默认图标。

## 构建

```powershell
powershell -File scripts/build-core.ps1      # Core + 单元断言
powershell -File scripts/build-agent.ps1     # Agent
powershell -File scripts/build-ui.ps1        # 仪表盘与设置
powershell -File scripts/build-deskband.ps1  # 原生 DeskBand（需要 MSVC）
powershell -File scripts/launch-dev.ps1      # 编译并启动开发实例
```

打包：

```powershell
powershell -File scripts/make-release.ps1 -Build
powershell -File scripts/build-installer.ps1
```

产物位于 `build\out\release\`。

## 目录结构

```text
src/
  QuadVitals.Agent/      传感器采集与共享内存发布（管理员进程）
  QuadVitals.Core/       快照契约、IPC、设置、格式化、反馈与更新
  QuadVitals.DeskBand/   原生 C++ COM DeskBand（Explorer 内运行）
  QuadVitals.Settings/   WPF 仪表盘与设置窗口
  QuadVitals.Setup/      单文件安装向导
  QuadVitals.UI/         WPF 设计系统
tests/                   Core 断言、UI 行为回归、安装器回归
scripts/                 构建、安装、测试与检查脚本
assets/                  产品图标（安装器与窗口图标；不随仓库分发，缺了用系统默认图标）
```

## 架构

```text
Agent（管理员）
  CPU / 内存 / 网络 / 温度 / 显卡 / 磁盘采样
        │  1 Hz 写入本地共享内存
        ▼
Local\QuadVitals.Metrics.v1
        │
        ├── DeskBand（Explorer 内只读、绘制）
        └── Settings（WPF 仪表盘与设置）
```

- DeskBand 不加载传感器驱动、不做阻塞 I/O、不访问网络；所有慢采样都在 Agent 的独立线程完成。
- 快照使用 seqlock 读取；版本或 Magic 不匹配时只降级显示，不抛出异常。
- 设置写入 `%APPDATA%\QuadVitals\settings.json`，Agent 与 DeskBand 自动热重载。
- 原生 DeskBand 在 Explorer 内运行，修改后需要重建 DLL、重启 Explorer 并重新勾选工具栏。

## 测试

```powershell
powershell -File scripts/verify.ps1
```

包含：

- Core 单元断言
- UI 行为回归
- 安装器行为回归
- Agent 临时目录编译
- 原生 DeskBand 编译检查
- 100% / 125% / 150% / 200% DPI 离屏渲染探针

修改行为时请同时补充断言；提交前建议先跑一次 `verify.ps1`。

## 提交约定

- 提交信息使用 Conventional Commits 前缀：`feat` / `fix` / `docs` / `test` / `chore` / `refactor` / `perf` / `build` / `ci`。
- 不提交构建产物、日志、本地设置或用户数据。
- 代码注释只解释“这段代码在防什么、改掉会坏什么”；编号、日期、他人原话与个人路径不写入代码。

## 文档

设计文档与用户文档由项目维护者本地保存，不随仓库发布。

## 许可证

与主仓库一致：PolyForm Noncommercial License 1.0.0，禁止商业用途。第三方组件许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
