# Windows 一键激活助手

基于微软官方 KMS 机制的图形化桌面工具：使用官方 GVLK 客户端密钥，调用系统自带 `slmgr` 完成激活。单一 exe、无需安装运行时、无需额外脚本，小白也能一键完成激活。

## 特性

- 单文件 exe（约 220 KB），Win10/11 双击即用（系统已内置 .NET Framework 4.8）
- 按当前系统版本自动匹配微软官方 GVLK 密钥，通过 `slmgr` 三步激活：安装密钥 → 设置 KMS 服务器 → 激活
- 全程无窗口、无弹窗，界面实时显示激活状态
- 已激活时按钮自动切换为「卸载激活」，一键卸载产品密钥恢复未激活
- 自动读取系统版本、内部版本、架构、安装类型与当前激活状态

## 使用方法

1. 下载 `publish/WindowsActivationHelper.exe`，双击打开（会请求管理员权限）。
2. 界面显示当前系统信息与激活状态。
3. 点击「一键激活」，等待状态变为绿色「已激活」即可。
4. 需要取消激活时，点击红色「卸载激活」。

支持的版本（微软官方 GVLK）：专业版、专业教育版、专业工作站版、企业版、教育版、企业版 LTSC/LTSB、IoT 企业版 LTSC 及其 N 版。家庭版不属于 KMS 批量授权范围，工具会给出明确提示。

## 编译

环境：Windows + .NET SDK（本项目使用 10.0.400 编译通过，目标框架 net48）。

```bat
build.cmd
```

产物：`publish\WindowsActivationHelper.exe`

## 目录结构

```text
src/MasActivator/                WPF 源码
  MainWindow.xaml               单页界面
  MainWindow.xaml.cs            系统信息读取、GVLK 匹配、slmgr 调用
  App.manifest                 DPI 感知与管理员权限清单
publish/WindowsActivationHelper.exe  成品
build.cmd                       一键编译脚本
```

## 免责声明

本工具仅供个人学习与测试使用。KMS 激活依赖第三方 KMS 服务器，有效期 180 天，到期可再次运行续期。请遵守所在地区的法律法规，商业环境请购买正版授权。使用产生的风险由使用者自行承担。

## 许可证

本项目遵循 [GPL-3.0](LICENSE)。
