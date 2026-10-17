# CampusNetGuard

CampusNetGuard 是一个面向 Windows 的校园网断线自动认证助手。程序以系统托盘方式运行，在确认有线校园网离线后动态解析门户登录表单并重新认证；有线连接持续断开时，可回退到已保存的校园 Wi-Fi 配置。

当前版本以 `v1.0.0` 作为首个可维护基线。该项目从已验证的个人使用版本整理而来，目标包括：

- 仅处理物理有线网卡和无线网卡，忽略 VPN、TAP 等虚拟适配器。
- 连续两次互联网探测失败后才尝试认证，减少短暂抖动造成的误判。
- 周日到周四的 `01:00-06:00` 计划断网时段完全暂停自动操作。
- 认证失败使用 `1、5、15、30` 分钟退避，并限制每小时尝试次数。
- 密码通过当前 Windows 用户的 DPAPI 加密保存，日志中不记录密码或 Cookie。
- 提供托盘开关、状态窗口、立即检测、账号设置、日志位置和开机启动。

## 环境要求

- Windows 10 或 Windows 11
- .NET Framework 4.8
- 可选的 Chrome 或 Edge，用于门户直接请求失败时的浏览器回退
- Wi-Fi 回退要求 `gdei-WiFi-S` 已作为 Windows 无线配置保存，且无线网卡和无线开关可用

程序默认使用 `asInvoker`，不请求管理员权限，也不运行 Windows 服务。它只在当前用户登录后工作，不负责唤醒睡眠或休眠的电脑。

## 构建

在 Windows PowerShell 中运行：

```powershell
.\scripts\build.ps1 -Configuration Release
```

脚本优先使用 Visual Studio 或 Windows 自带的 MSBuild。若本机缺少 .NET Framework 4.8 targeting pack，会自动回退到系统 .NET Framework 编译器。也可以显式使用回退路径：

```powershell
.\scripts\build.ps1 -Configuration Release -UseCompilerFallback
```

构建结果位于 `artifacts/bin/Release/CampusNetGuard.exe`。

## 测试

```powershell
.\scripts\test.ps1 -Configuration Release
```

测试是离线回归测试，不会提交真实校园网凭据，也不会执行退出登录、强制断网或真实门户认证。真实拔线和真实认证只在用户在场时人工验证。

## 打包

```powershell
.\scripts\package.ps1 -Version 1.0.0
```

发布包生成在 `artifacts/releases`，同时生成 SHA-256 校验文件。

## 目录结构

```text
src/CampusNetGuard/                应用程序源码
tests/CampusNetGuard.SmokeTests/   离线回归测试
docs/                              使用说明和设计文档
scripts/                           构建、测试和打包脚本
.github/                           CI、缺陷模板和 PR 检查项
```

## 日常维护流程

1. 从 `main` 建立短分支，例如 `fix/portal-timeout`。
2. 修改代码，并补充可以离线执行的回归测试。
3. 运行构建和测试脚本。
4. 更新 `CHANGELOG.md`。
5. 提交后创建 Pull Request，确认 CI 通过再合并。
6. 发布时打语义化版本标签，例如 `v1.0.1`。

详细操作流程见 [docs/operations.md](docs/operations.md)，架构说明见 [docs/architecture.md](docs/architecture.md)。

## 安全

- 不要提交 `settings.xml`、`app-*.log`、Cookie、Chrome profile 或门户抓取结果。
- 密码仅由程序在用户本机输入，并使用当前用户 DPAPI 保存。
- DPAPI 密文不能跨 Windows 用户或电脑直接迁移；更换环境后需要重新输入密码。
- 安全问题请按 [SECURITY.md](SECURITY.md) 的方式处理。

## 已知限制

- 门户页面和认证协议变化后，程序可能需要更新兼容逻辑。
- 无线硬件关闭时，程序不会请求管理员权限强行启用设备。
- 用户注销、睡眠或休眠期间不能自动认证。
- 当前 `1.0.0` 基线保留一个已知日志文案问题：进入 Wi-Fi 回退状态时可能提前记录“校园网已恢复”。后续修复应增加回归测试并单独提交。
