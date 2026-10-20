# OnlyShell

OnlyShell 是一款面向 Windows 的 SSH 工作台，提供交互式终端、SFTP 文件管理和 GitHub 加密配置同步。界面采用紧凑的代码工作区风格：直角边框、深色背景、琥珀色强调，以及青绿状态提示。

> 当前项目处于早期开发阶段，尚未提供正式安装包。使用前请先从源码构建，并在测试环境验证连接和传输行为。

## 功能

- **主机管理**：按分组保存、搜索、编辑主机资料，并记录主机指纹。
- **SSH 终端**：多标签交互式终端，支持密码或 SSH 私钥认证、断线状态提示和手动重连。
- **本地终端与工作区恢复**：在同一标签工作区启动 Windows PowerShell 或 CMD；应用重启后重新打开本地 shell 标签，SSH 标签以未连接状态恢复并可手动重连。
- **脚本中心**：本地 PowerShell / CMD / Python 3 与远程 Bash / Python 3；支持多级目录、搜索、类型化参数/环境变量模板、必填校验、加密默认值和逐项运行输入、实时输出、取消及近期运行记录。参数默认值与运行输入按当前 Windows 用户使用 DPAPI 加密保存在本机，不进入脚本定义、运行历史或 GitHub 同步。
- **服务器监控**：SSH 连接期间采集 Linux CPU、内存、Swap、根磁盘、负载、网络速率、运行时间、进程数和 SSH 往返时间；不需安装服务端代理。
- **SFTP 文件管理**：本地与远程双栏浏览，可切换本地盘符、多选文件与目录、拖放传输、递归传输目录，并查看/取消传输队列任务；失败任务可在同一主机重新连接后手动重试。单文件上传和下载在替换目标前会校验传输前后的文件大小；递归操作会跳过符号链接。
- **诊断日志**：应用异常日志保存在 `%LOCALAPPDATA%\OnlyShell\logs\`；设置页可导出运行环境信息和日志组成的 ZIP 诊断包。
- **GitHub 同步**：使用 GitHub App Device Flow 授权，将主机资料加密后同步至指定的私有仓库；远端版本冲突会阻止覆盖。
- **本机凭据保护**：SSH 密码和 GitHub 访问令牌存放在当前 Windows 用户的凭据管理器中。

## 安全与同步范围

同步载荷只包含主机名称、地址、端口、用户名、分组、收藏状态、备注、认证类型和最近连接时间。载荷在上传前使用 AES-GCM 加密，密钥由用户输入的同步口令经 PBKDF2-SHA256 派生。

SSH 密码、私钥内容、私钥口令和本机私钥文件路径不会进入同步数据。拉取配置时，同一主机 ID 对应的本机密码凭据引用和私钥路径会保留；远端没有的主机会在确认后从本机列表移除。加密口令不保存在应用中，遗忘后无法解密云端数据。

## 环境要求

- Windows 10/11
- .NET 10 SDK（`global.json` 指定 10.0.100，并允许使用同一 feature band 的更新版本）
- 可访问 GitHub 的网络，用于 Device Flow 登录和同步

## 构建与启动

在仓库根目录打开 PowerShell 开发运行：

```powershell
dotnet restore OnlyShell.sln
dotnet build OnlyShell.sln
dotnet run --project src/OnlyShell.App/OnlyShell.App.csproj
```

构建可分发的 Windows x64 免安装目录及 ZIP 包：

```powershell
.\scripts\publish-windows.ps1
```

输出位于 `artifacts\publish\win-x64\` 和 `artifacts\OnlyShell-win-x64.zip`。正式安装包和更新发布说明见 [`docs/发布Windows版本.md`](docs/发布Windows版本.md)。推送 `v*` 标签后，GitHub Actions 会构建自包含版与依赖 .NET Runtime 版，并创建对应的 Releases；安装器提供安装目录选择，安装后的更新由 Velopack 管理。目前发布资产未进行代码签名。

应用数据保存在 `%LOCALAPPDATA%\OnlyShell\`，包括 SQLite 主机数据库和同步设置；访问令牌本身保存在 Windows 凭据管理器。

## 配置 GitHub 同步

1. 在 GitHub 创建一个专用私有仓库，并记下仓库所有者、名称和分支。OnlyShell 会在仓库中维护 `.onlyshell/config.v1.json`。
2. 使用 GitHub App Device Flow 登录。应用所用的 GitHub App 必须启用 **Device Flow**，并授予 Repository permissions 中的 **Contents: Read and write**。Webhook 与 Client Secret 不需要。
3. 在 OnlyShell 的“同步”页填写仓库信息，首次使用时执行 **ENCRYPT + PUSH**；其他设备登录后执行 **PULL / REPLACE LOCAL**。
4. 每台设备输入同一个同步加密口令。拉取会替换本地主机列表，应用会在操作前再次确认。

如果你是从源码构建自己的 OnlyShell 实例，请在 `src/OnlyShell.App/GitHubSyncService.cs` 中将 `ClientId` 替换为你注册的 GitHub App Client ID，并确认该 App 已配置上述 Device Flow 与仓库权限。Client ID 是公开标识；不要把 Client Secret 或用户访问令牌提交到仓库。

## 项目结构

```text
src/
  OnlyShell.App/                    WPF 应用与工作区界面
  OnlyShell.Presentation/           ViewModel 与界面服务契约
  OnlyShell.Application/            应用层
  OnlyShell.Core/                   领域模型与接口
  OnlyShell.Infrastructure.Data/    SQLite 持久化
  OnlyShell.Infrastructure.Secrets/ Windows 凭据管理器适配
  OnlyShell.Infrastructure.Ssh/     SSH.NET 与 SFTP 适配
  OnlyShell.Infrastructure.Sync/    GitHub API、Device Flow 与载荷加密
  OnlyShell.Terminal/               终端适配层
TerminalPoc/                         独立终端验证项目及当前复用的 VT 源码
design/prototypes/                  UI 概念图
docs/                                工程基线与实现规划
```

`OnlyShell.Terminal` 当前复用 `TerminalPoc/VirtualTerminal-upstream/` 中验证过的终端源码。该目录包含上游 Apache-2.0 许可文件；发布前应继续核对第三方许可与版本信息。

## 界面原型

工作台、终端和 SFTP 概念图见 [`design/prototypes/`](design/prototypes/)。原型用于表达布局和视觉方向，图中的示例数据并非产品承诺。

## 已知限制

- 当前只支持 Windows 桌面端；提供免安装发布包，并配置正式安装器与 GitHub Releases 自动更新工作流。发布资产暂未进行代码签名。
- SSH 终端断线重连会保留当前窗口缓冲区，但不会恢复远端 shell 进程或重放命令。
- 工作区恢复只保存标签类型、SSH 主机 ID、本地 shell 类型、初始工作目录、选中标签和当前页面。重启后本地 shell 是新进程，SSH 需手动重连；命令历史、进程状态和终端缓冲区不会恢复。
- SFTP 递归传输遇到符号链接时会跳过链接，避免意外跟随到其他目录。
- 同步采用整份主机配置的推送/拉取，不做字段级合并；跨设备同步前请先确认目标设备上的本地主机资料。
- 同步拉取在 SQLite 事务中整体替换主机列表，写入失败会回滚；错误的同步口令会提示检查口令或远端数据完整性。
- 服务器监控目前仅实现 Linux 主机采集，按连接会话轮询；尚无历史趋势图或 Windows 主机指标采集。
- 脚本中心支持远程脚本逐台批量执行，暂不支持并发执行。运行参数和环境变量以当前 Windows 用户 DPAPI 加密并留在本机；脚本运行输出会以明文保存在本机运行历史中，可能包含敏感信息，脚本回显的内容会记录。
- SFTP 上传和下载失败后可在同一目标路径重试续传；续传前校验已有部分文件的前缀 SHA-256，完成后校验全文件 SHA-256。目录传输中已完成文件会在重试时重新传输。断点续传会额外读取远端数据，遇到符号链接会跳过；权限编辑和递归删除尚未实现。
- 已选择“记住密码”的主机会优先使用当前 Windows 用户凭据管理器中的密码自动连接；保存密码缺失或认证失败时才会提示重新输入。未选择保存密码的连接仍会按需提示。

## 许可证

本项目尚未声明整体开源许可证。`TerminalPoc/VirtualTerminal-upstream/` 中的第三方组件遵循其随附的 Apache-2.0 许可。
