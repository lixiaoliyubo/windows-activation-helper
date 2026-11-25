# Windows 一键激活助手

基于 [Microsoft Activation Scripts（MAS）](https://github.com/massgravel/Microsoft-Activation-Scripts) 制作的图形化桌面工具。单一 exe、无需安装 Python/NET 运行时、无需联网下载脚本，小白也能一键完成激活。

## 特性

- 单文件 exe，约 770 KB，Win10/11 双击即用（系统已内置 .NET Framework 4.8）
- 内置 MAS 3.14 官方 `MAS_AIO.cmd`，激活过程不依赖外部脚本文件
- 单页界面：标题、激活教程、Windows 版本信息、激活类型、一键激活、版本与版权
- 自动读取系统版本、内部版本、架构、安装类型与当前激活状态
- 五种激活方式可选，点击按钮后自动申请管理员权限并调用官方无人值守参数

## 使用方法

1. 下载 `publish/WindowsActivationHelper.exe`，双击打开。
2. 点击「打开官方文档」访问 <https://yuandl.cn>，查看各激活方式的说明。
3. 在「选择激活类型」下拉框中选择一种方式：
   - **Windows 数字激活（HWID）**：推荐，自动匹配版本，重装系统后仍可激活
   - **Office 激活（Ohook）**：Office 2016—2024 / Microsoft 365
   - **Windows + Office 双激活**：一次完成两者
   - **TSforge 激活**：Windows / ESU / Office 兼容性方案
   - **KMS 激活（180 天）**：临时或批量环境，需要联网
4. 点击「一键激活」，弹出授权窗口时选择「是」。
5. 等待黑色命令行窗口自动结束，回到界面查看最新激活状态。

## 编译

环境：Windows + .NET SDK（本项目使用 10.0.400 编译通过，目标框架 net48）。

```bat
build.cmd
```

产物：`publish\WindowsActivationHelper.exe`

编译原理：使用 SDK 风格 WPF 项目 + `Microsoft.NETFramework.ReferenceAssemblies.net48` 引用程序集，把 `MAS_AIO.cmd` 作为 `EmbeddedResource` 编入 exe，因此最终只有一个可执行文件。

## 目录结构

```text
src/MasActivator/                WPF 源码
  MainWindow.xaml               单页界面
  MainWindow.xaml.cs            系统信息读取、脚本释放、激活调用
  App.manifest                  DPI 感知与权限清单
  Resources/MAS_AIO.cmd         内置 MAS 官方脚本
publish/WindowsActivationHelper.exe  成品
build.cmd                       一键编译脚本
LICENSE                         GPL-3.0（跟随 MAS 上游许可）
```

## 免责声明

本工具仅为 MAS 的图形化封装，供个人学习与测试使用。请遵守所在地区的法律法规，商业环境请购买正版授权。使用脚本激活产生的风险由使用者自行承担。

## 许可证

本项目遵循 [GPL-3.0](LICENSE)。上游脚本版权归 massgravel 及贡献者所有，项目地址：<https://github.com/massgravel/Microsoft-Activation-Scripts>
