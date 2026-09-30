# 开发环境搭建（当前 .NET 主线）

> 当前唯一可运行主线是仓库根目录下的 `src/`。本文只描述已落地的本地开发与验证流程。

## 1. 环境要求

支持 Windows、macOS 和 Linux。

必需：

- .NET SDK 9.0.200 或更高版本：用于读取 `src/BankingAgent.slnx`
- .NET 8 Runtime 与 Targeting Pack：所有项目目标框架均为 `net8.0`
- Git：仅获取代码和协作时需要

本机验证环境示例为 .NET SDK 9.0.305、.NET Runtime 8.0.20。它是已验证组合，不是全队必须精确锁定的版本。

默认本地运行不需要 PostgreSQL、Docker、Node.js、Python、Redis、Kafka 或 AI Key。数据使用 SQLite 文件保存；控制台由 `BankingAgent.Host/wwwroot` 内置提供，不需要单独构建前端。

先确认环境：

```powershell
dotnet --version
dotnet --list-sdks
dotnet --list-runtimes
```

`dotnet --version` 应不低于 9.0.200，运行时列表应包含 `Microsoft.NETCore.App 8.0.x` 和 `Microsoft.AspNetCore.App 8.0.x`。若无法编译 `net8.0`，请安装 .NET 8 SDK 或对应 Targeting Pack。

## 2. 编译

以下命令均从仓库根目录执行。插件复制与运行配置相关，必须统一使用 Release。

```powershell
dotnet restore src/BankingAgent.slnx
dotnet build src/BankingAgent.slnx -c Release
```

编译后检查四个插件 DLL：

```powershell
$pluginDir = "src/src/BankingAgent.Host/bin/Release/net8.0/plugins"
$required = @(
  "BankingAgent.Plugin.Transfer.dll",
  "BankingAgent.Plugin.BillAnalysis.dll",
  "BankingAgent.Plugin.CardManagement.dll",
  "BankingAgent.Plugin.Wealth.dll"
)

$required | ForEach-Object {
  $path = Join-Path $pluginDir $_
  if (-not (Test-Path $path)) { throw "缺少插件产物：$path" }
}
Get-ChildItem $pluginDir -Filter "*.dll"
```

## 3. 启动

打开两个 PowerShell 终端，均停留在仓库根目录。

终端 1：启动模拟银行。

```powershell
dotnet run --project src/mock-bank/MockBank.Api -c Release
```

终端 2：启动 Agent 宿主。

```powershell
dotnet run --project src/src/BankingAgent.Host -c Release
```

两个进程分别为：

| 进程 | 地址 | 用途 |
|---|---|---|
| `MockBank.Api` | `http://localhost:5200` | 模拟银行核心系统 |
| `BankingAgent.Host` | `http://localhost:5243` | Agent API 与内置 Web 控制台 |

浏览器打开 <http://localhost:5243> 即可使用控制台。

## 4. 停止与重启

前台运行时，在各自终端按 `Ctrl+C` 停止。若终端已关闭，可按监听端口定位进程：

```powershell
Get-NetTCPConnection -LocalPort 5200,5243 -State Listen |
  Select-Object LocalPort, OwningProcess

# 确认 PID 后再停止；不要直接复制示例 PID
Stop-Process -Id <PID>
```

macOS/Linux 可用 `lsof -i :5200 -i :5243` 查找进程，再用 `kill <PID>` 停止。

## 5. 健康检查与验收

PowerShell 7 可直接使用 `curl`；Windows PowerShell 建议使用 `Invoke-RestMethod`：

```powershell
Invoke-RestMethod http://localhost:5200/health
Invoke-RestMethod http://localhost:5243/health
```

本地开发验收：

1. `dotnet build src/BankingAgent.slnx -c Release` 成功。
2. Release 输出目录存在四个插件 DLL。
3. 5200 与 5243 两个端口均处于监听状态。
4. 两个 `/health` 均成功返回。
5. 打开 <http://localhost:5243>，可以登录、发送消息并看到确认卡片。

## 6. 测试与工具

```powershell
# 单元测试
dotnet test src/UnitTests/UnitTests.csproj -c Release

# 两个服务已启动后执行
dotnet run --project src/E2ETest -c Release -- http://localhost:5243 http://localhost:5200
dotnet run --project src/StressTest -c Release -- http://localhost:5243 http://localhost:5200

# 插件契约校验
dotnet run --project src/PluginValidator -c Release -- `
  src/src/BankingAgent.Host/bin/Release/net8.0/plugins
```

EF Core CLI 由 `src/.config/dotnet-tools.json` 锁定为 8.0.10：

```powershell
Push-Location src
dotnet tool restore
dotnet ef --version
Pop-Location
```

## 7. 可选增强

### 7.1 大模型意图识别

默认规则引擎不需要 AI Key。需要增强意图识别时，可在当前终端临时注入：

```powershell
$env:Ai__ApiKey = "sk-xxx"
dotnet run --project src/src/BankingAgent.Host -c Release
```

不要把密钥写入仓库。模型不可用时会自动回退到规则表。

### 7.2 PostgreSQL

当前本地默认且已验证的路径是 SQLite。代码具备 PostgreSQL Provider 配置能力，但 PostgreSQL 属于生产化演进选项，需要单独准备实例、连接字符串、迁移和密钥管理，不是本地启动前置条件。

## 8. 常见故障

### `.slnx` 无法识别

原因通常是当前 SDK 低于 9.0.200。运行 `dotnet --version`，安装或切换到满足要求的 SDK。

### 提示缺少 .NET 8 运行时或引用程序集

SDK 9 可读取解决方案，但程序目标仍是 `net8.0`。安装 .NET 8 Runtime；编译缺少引用程序集时，再安装 .NET 8 SDK 或 Targeting Pack。

### 插件缺失或依赖未找到

最常见原因是 Release 编译后用默认 Debug 启动。重新执行：

```powershell
dotnet build src/BankingAgent.slnx -c Release
dotnet run --project src/src/BankingAgent.Host -c Release
```

再检查 `src/src/BankingAgent.Host/bin/Release/net8.0/plugins/` 是否包含四个 DLL。

### 5200 或 5243 端口被占用

```powershell
Get-NetTCPConnection -LocalPort 5200,5243 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
Get-Process -Id <PID>
```

确认是残留进程后停止它；本项目的默认联调地址固定使用 5200 和 5243，临时改端口时还需同步修改宿主的 `CoreBank:BaseUrl`。

### `/health` 失败

先确认 MockBank 已启动，再查看两个终端的启动日志。宿主健康检查包含真实数据库探活；SQLite 文件不可写、进程工作目录异常或数据库文件被占用都会导致失败。

### AI Key 配置后未生效

```powershell
Test-Path Env:Ai__ApiKey
$env:Ai__ApiKey
```

环境变量会覆盖配置文件，包括空字符串。清除残留值后重启宿主：

```powershell
Remove-Item Env:Ai__ApiKey
```

## 9. 关联文档

- 快速运行与插件排查：[`../plugin/00-quick-start.md`](../plugin/00-quick-start.md)
- CI：[`05-ci-cd-pipeline.md`](05-ci-cd-pipeline.md)
- 部署说明：[`../03-deploy/部署说明.md`](../03-deploy/部署说明.md)
- 版本清单：[`../03-deploy/运行环境与依赖版本清单.md`](../03-deploy/运行环境与依赖版本清单.md)