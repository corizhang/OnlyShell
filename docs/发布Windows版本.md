# 发布 Windows 版本

OnlyShell 提供两种 Windows x64 安装包：

- **Self-contained**：包含 .NET 10 Desktop Runtime，不要求用户预装 .NET；下载体积较大。
- **Framework-dependent**：不把 .NET Runtime 放进应用包。安装时 Velopack 会检查并在缺少 .NET 10 Desktop Runtime 时下载安装；需要网络，应用目录更小。

两种版本都使用带安装目录选择页的 Inno Setup 向导安装。Inno Setup 负责用户可见的安装界面；它会静默调用 Velopack 完成应用部署，结束页由 Inno Setup 提供启动应用选项。安装后由 Velopack 管理应用文件和自动更新。应用数据保存在 `%LOCALAPPDATA%\OnlyShell`，不放入安装目录。

## 本地生成

发布两种版本的应用，再分别用 VPK 生成 Velopack 更新包。下面是自包含版示例；Framework-dependent 版将 `--self-contained true` 改为 `false`，并在 `vpk pack` 添加 `--framework net10.0-x64-desktop`。之后用仓库中的 Inno Setup 脚本生成带路径选择的安装向导：

```powershell
dotnet publish src/OnlyShell.App/OnlyShell.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/self-contained -p:Version=1.0.0
dotnet tool install --global vpk --version 1.2.0
vpk pack --packId OnlyShellSelfContained --packTitle OnlyShell --packVersion 1.0.0 --packDir artifacts/publish/self-contained --mainExe OnlyShell.exe --channel win-x64-self-contained --outputDir artifacts/velopack/self-contained --icon src/OnlyShell.App/Assets/OnlyShell.ico
```

VPK Setup.exe 默认安装时不显示目录选择页，因此工作流使用 Inno Setup 包装器调用 `Setup.exe --installto`，安装目录由 Inno 向导收集。

GitHub Actions 工作流 `Release Windows` 会在推送 `v*` 版本标签后构建两种变体，并为每种变体建立单独的 GitHub Release 和更新通道。标签格式使用 `v主版本.次版本.修订版本`，例如：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

发布前请确认 GitHub Actions 对仓库拥有 `contents: write` 权限。工作流会用 `GITHUB_TOKEN` 上传 Velopack 更新资产。

## 安装与更新

- `OnlyShell-SelfContained-<版本>-Setup.exe` 可以在没有 .NET 环境的 Windows x64 机器上安装。
- `OnlyShell-FrameworkDependent-<版本>-Setup.exe` 安装时会联网安装 .NET 10 Desktop Runtime（如果尚未安装）。
- 安装器会让用户选择安装路径。应用后续启动时检查 GitHub Releases；发现新版本后询问用户，确认后下载并重启完成更新。
- 两种版本走独立更新通道。自包含版更新不会切换成 Framework-dependent，反之亦然。

当前更新源指向仓库 `corizhang/OnlyShell`。若要让所有用户自动更新，Releases 资产必须可匿名读取；若仓库是私有的，不能把有写权限的 GitHub token 嵌入桌面程序，需要单独配置公开更新托管或面向用户的认证服务。应用每 24 小时最多检查一次，以减少 GitHub 匿名 API 请求。

正式面向用户发布时建议对安装器和应用做代码签名，以减少 SmartScreen 警告并帮助用户确认发布者身份。当前工作流尚未绑定签名证书。

## 发布前检查

在干净的 Windows 10/11 x64 环境分别安装两个版本，检查应用启动、用户选择的安装路径、首次启动、更新下载与重启、卸载、SSH/SFTP、脚本执行、本地数据迁移和凭据访问。Framework-dependent 版本还应在没有 .NET Desktop Runtime 的环境中验证联网引导安装流程。
