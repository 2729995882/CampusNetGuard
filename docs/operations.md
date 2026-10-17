# 维护与发布流程

## 分支与提交

- `main` 始终保持可构建、可测试。
- 功能或修复使用短分支，例如 `fix/portal-timeout`。
- 每个提交只解决一个清晰问题。
- 合并前必须通过构建和离线测试。

提交示例：

```text
fix: avoid false recovery log during Wi-Fi fallback
test: cover recovery state transitions
docs: update portal troubleshooting notes
```

## 变更检查

1. 运行 `scripts\build.ps1 -Configuration Release`。
2. 运行 `scripts\test.ps1 -Configuration Release`。
3. 如果修改了门户解析，增加脱敏 HTML 样本和离线测试。
4. 如果修改了网络或 Wi-Fi 行为，在用户在场时人工验证。
5. 更新 `CHANGELOG.md`。

## 发布

1. 更新程序集版本和 `CHANGELOG.md`。
2. 运行 `scripts\package.ps1 -Version X.Y.Z`。
3. 检查生成的 zip 和 SHA-256 文件。
4. 创建并推送版本标签。
5. 在 GitHub Release 中附上 zip、校验文件和变更说明。

## 回退

发布前保留上一个稳定版本。出现严重问题时：

1. 将 `main` 回退到上一个稳定标签。
2. 发布补丁版本并注明回退原因。
3. 保留脱敏日志，不使用含真实凭据或 Cookie 的原始抓包作为仓库测试样本。
