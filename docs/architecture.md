# CampusNetGuard 架构说明

## 总体结构

程序使用 .NET Framework 4.8 WinForms 实现，以 `GuardContext` 作为状态机和调度中心。Windows 网络变化事件负责即时触发，30 秒定时器负责兜底检查。

```text
Windows 网络事件 / 30 秒定时器 / 托盘操作
                    |
                    v
               GuardContext
                    |
      +-------------+-------------+
      |             |             |
 NetworkUtils   PortalAuth   WifiManager
      |             |             |
      +------- 状态与重试策略 -----+
                    |
          Settings / Logger / UI
```

## 主要模块

| 模块 | 职责 |
| --- | --- |
| `Program` | 入口、单实例和程序生命周期 |
| `GuardContext` | 状态机、30 秒重试调度、暂停和恢复策略 |
| `NetworkUtils` | 物理网卡识别、互联网探测、Wi-Fi 操作 |
| `PortalAuthenticator` | 门户页面加载、表单解析、认证提交和响应判断 |
| `BrowserPortalFallback` | 使用本机 Chrome/Edge 兜底执行门户登录 |
| `Models` | 设置、状态、门户表单和路径模型 |
| `Ui` | 托盘菜单、状态窗口和设置界面 |

## 核心不变量

- 网络正常时不访问门户。
- 连续两次探测失败后才进入认证。
- 探测和认证绑定当前物理网卡地址。
- 计划暂停期间不探测、不认证、不切换 Wi-Fi。
- 普通认证失败每 30 秒重试，浏览器回退最多每 2 分钟启动一次。
- 只有 Wi-Fi 软件无线电关闭时才尝试自动开启，硬件开关关闭时停止并提示用户。
- 凭据错误和人工验证要求会停止自动提交。
- 日志、配置和凭据不得进入源码仓库。
