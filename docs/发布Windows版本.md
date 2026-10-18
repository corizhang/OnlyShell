# 发布 Windows 版本

仓库当前提供 Windows x64 自包含免安装发布流程。它将 .NET 运行时与 WPF 应用一并发布，用户不需要单独安装 .NET Runtime。

## 生成发布包

在 Windows PowerShell 中，从仓库根目录执行：

```powershell
.\scripts\publish-windows.ps1 -Version 1.0.0
```

结果：

- `artifacts\publish\win-x64\OnlyShell.exe`：可直接启动的发布目录。
- `artifacts\OnlyShell-win-x64.zip`：发布目录压缩包。

脚本调用 `dotnet publish` 的 Release、`win-x64`、self-contained 配置。第一次发布仍需还原 NuGet 包；网络或 NuGet 源不可用时，先处理还原错误再发布。

## 发布前检查

在干净的 Windows 10/11 x64 环境解压验证：

1. 应用正常启动，设置页能显示日志位置并导出诊断 ZIP。
2. 密码和私钥 SSH 连接、指纹确认、断线重连。
3. 本地 PowerShell/CMD/Python 脚本与远程 Bash/Python 脚本输出及取消。
4. SFTP 上传、下载、递归目录、覆盖确认和取消。
5. 应用重启后的主机数据、凭据引用和工作区恢复行为。

## 当前不包含

目前发布脚本不生成 MSI/MSIX 安装程序，不签署可执行文件，也不实现自动更新。面向公众发布前还需确定产品版本策略、项目许可证、第三方依赖许可、代码签名证书与更新托管方案。
