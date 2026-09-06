# 编辑器自动化

## 固定的唯一桥接

CoplayDev MCP for Unity `v10.2.0`，UPM commit：`30d22075093d1d35dfb0091c1c7550e9ad948577`。

UPM URL：

```text
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577
```

Python server：`mcpforunityserver==10.2.0`。本项目本地 uv `0.12.10`，Python 复用 `3.10.1`，不写系统 PATH。
服务只使用 `http://127.0.0.1:8080/mcp`；依赖与本机配置在被忽略的 `.local`、`.codex` 中。

`Tools/Start-UnityBridge.ps1` 启动本项目的唯一 server，发现占用或其他实例时停止，不自动改客户端配置。
`Tools/codex-unity.example.toml` 为可版本管理的配置样例，本机实际配置为 `.codex/config.toml`。
当前任务未必会热加载刚写入的配置，写入不等于连通；连通验收需要实际 initialize、tools/list、指定实例的工具调用。

## 在新检出中准备本地依赖

使用 PowerShell **7.4 或以上**（本机已验证 7.6.5）；启动脚本使用子进程专属的 `-Environment` 参数。本机以下依赖已安装，不要重复运行安装。新检出时，确认 `.local/python-tools` 和 `.local/bridge-venv` 尚不存在，再运行下面的初始化命令；若已有目录，先检查现有版本，不覆盖未知环境。

Python 必须为 3.10 或以上。下面的 Python 路径是本机已验证的 3.10.1；换机器时仅将该变量改为已安装解释器的实际路径。安装目标、下载缓存和虚拟环境均在本项目 `.local`，不修改系统 PATH 或全局 Python 包。

```powershell
Set-Location -LiteralPath 'M:\GoldCamp'
$projectPython = 'C:\Users\yumin\AppData\Local\Programs\Python\Python310\python.exe'
& $projectPython --version
& $projectPython -m pip install --index-url 'https://pypi.org/simple' --cache-dir '.local\pip-cache' --target '.local\python-tools' 'uv==0.12.10'
if ($LASTEXITCODE -ne 0) { throw '本地 uv 安装失败；先检查输出，不重复启动安装。' }

$uvPath = 'M:\GoldCamp\.local\python-tools\bin\uv.exe'
$venvPython = 'M:\GoldCamp\.local\bridge-venv\Scripts\python.exe'
& $uvPath venv --python $projectPython --no-python-downloads --cache-dir '.local\uv-cache' '.local\bridge-venv'
if ($LASTEXITCODE -ne 0) { throw '项目虚拟环境创建失败。' }
& $uvPath pip install --python $venvPython --default-index 'https://pypi.org/simple' --cache-dir '.local\uv-cache' 'mcpforunityserver==10.2.0'
if ($LASTEXITCODE -ne 0) { throw '固定版本的桥接安装失败。' }
& $uvPath pip check --python $venvPython --cache-dir '.local\uv-cache'
if ($LASTEXITCODE -ne 0) { throw '桥接依赖检查未通过。' }
& $uvPath pip freeze --python $venvPython --cache-dir '.local\uv-cache' | Set-Content -LiteralPath '.local\bridge-dependencies.txt' -Encoding utf8
```

直接依赖固定为上述版本，来源为 PyPI；间接依赖的实际解析结果写入本机 `bridge-dependencies.txt`。新机器若重新解析，不能仅凭 server 版本相同就声称所有间接依赖完全相同。本轮实际安装了 75 个包，`uv pip check` 通过，CLI `--help` 退出码为 0。

已有环境只需检查版本，不重新安装：

```powershell
& 'M:\GoldCamp\.local\python-tools\bin\uv.exe' --version
& 'M:\GoldCamp\.local\bridge-venv\Scripts\python.exe' -c 'import importlib.metadata as m; print(m.version("mcpforunityserver"))'
```

## 启动与运行 Probe

先启动服务；脚本会复用本项目已运行的 server，若发现其他桥接或 8080 被占用则拒绝另起服务。`Started` 或 `AlreadyListening` 只说明进程／监听状态，不代表 Unity 已连接。

```powershell
Set-Location -LiteralPath 'M:\GoldCamp'
.\Tools\Start-UnityBridge.ps1
& '.\.local\bridge-venv\Scripts\python.exe' '.\Tools\Probe-UnityBridge.py' --action inspect
```

随后打开本项目 Unity 编辑器，使用菜单 **GoldCamp > M0 > Open scene and connect MCP**。该项目入口打开 `Assets/GoldCamp/Scenes/M0_Graybox.unity` 并连接已有 server，不配置客户端。等待编译完成，确保场景已经保存、只加载这一张场景且未进入 Play，再执行一次 `inspect` 获取实际连接实例。

`inspect`（或 `--action instances`）只执行 MCP 初始化、列出工具／资源并读取实例清单；零实例说明尚未连接，不能继续 smoke。`initialize.serverInfo.version` 本轮返回的是 FastMCP 框架版本 3.4.7，不能将它误写成 `mcpforunityserver` 的版本；探针另行记录实际 distribution 版本 10.2.0。

下面的完整实例 ID 是本轮实测示例。**仅当本次 inspect 仍返回此 ID 时使用**，否则替换为本次返回的完整 `name@hash`，不能使用名字、hash 前缀或默认实例：

```powershell
$unityInstance = 'GoldCamp.Unity@88a7ad00686047e7'
& '.\.local\bridge-venv\Scripts\python.exe' '.\Tools\Probe-UnityBridge.py' --action smoke --unity-instance $unityInstance --scene-path 'Assets/GoldCamp/Scenes/M0_Graybox.unity'
```

smoke 在任何场景写入前，会验证实际 `projectRoot`、编辑器版本、完整实例 ID、唯一加载的已保存场景和非 Play 状态。它只创建或修改一个根级空对象 `M0_BridgeProbe`，位置为 `(0,-2,0)`，组件只允许 Transform；发现重名对象、额外组件、子对象或已有未保存改动时会拒绝继续。随后保存场景，再读对象、场景和 Console。已有该空对象时会复用，不生成重复对象。

每次执行输出独立的 `Builds/Logs/unity-bridge-probe-*.json` 脱敏报告，包含真实返回值、所选实例和成功／失败状态。失败时先读报告；不要把一次失败前的部分操作当成整个 smoke 已通过。检查完后关闭本项目交互编辑器，再运行批处理测试或构建。

## 必须实际验收

1. 读取 `mcpforunity://instances`，获取实际名称、hash、完整 ID 和 Unity 版本。HTTP 模式的该资源不返回项目路径，不能据此推断工作目录。
2. 用实际完整 ID 调用 `set_active_instance` 设置本次 MCP 会话的资源路由，再读 `mcpforunity://project/info`，要求 `projectRoot` 规范化后精确等于 `M:\GoldCamp\GoldCamp.Unity`、`unityVersion` 等于 `6000.3.23f1`。不能仅凭产品名判断项目；每次工具调用还须附带同一完整 `unity_instance`。上述 Probe 已实现这些检查。
3. `read_console` 读真实 Console；`manage_scene` 查询 hierarchy。
4. 查询后创建/修改唯一 `M0_BridgeProbe`，保存场景，再次查询并确认落盘。
5. 超时写操作先查询结果再决定重试。

若桥接兼容失败，使用 `Tools/Invoke-Unity.ps1 -Action SmokeAutomation` 调用项目 Editor API，
`Builds/Logs/editor-automation.txt` 和 Unity 本次日志提供查询、修改、保存、重开证据。
替代路径成功必须写作 Editor API 自动化，不冒称 MCP 已连通。

## 核对来源

- [维护者 v10.2.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.2.0)
- [维护者安装说明](https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/website/docs/getting-started/install.md)
- [实例路由限制](https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/website/docs/architecture/instance-routing.md)
- [官方 Codex MCP 项目配置](https://learn.chatgpt.com/docs/extend/mcp)

实际安装、连通和运行结果以 `STATUS.md` 为准；本文件是操作入口和验收标准。

## 上游自动配置副作用与本项目保护

v10.2.0 的 `StartupConfigRewrite` 在非 batch 编辑器启动时默认扫描客户端，可能重写用户级配置，不能仅靠“不点击自动配置”避免。
本项目 `M0ClientConfigGuard` 在程序集初始化时设置上游公开的会话 guard，阻止该延迟扫描。保护只用 Unity SessionState，不改全局 EditorPrefs。

首次 GUI 打开前尚未有此保护，插件于本机 18:47 自动写入全局 Codex 的 Unity MCP 配置。经用户授权处理后，已备份并只移除指向本项目 loopback 服务的 `mcp_servers.unityMCP` 条目；其他现有文本保持不动。插件写入前的历史快照不可恢复，因此不猜测或回滚通用 `features.rmcp_client` 的旧值。备份保留在本机，绝不提交 Git。

后续请使用 GoldCamp 项目菜单连接，不使用插件的客户端自动配置按钮。升级桥接时必须重新核验该保护，并比较启动前后全局配置哈希。

2026-09-05 已重新启动 GUI 编辑器并连接 MCP，清理后的全局配置 SHA-256 保持不变，且全局 Unity MCP 条目仍不存在。证据：`Builds/Logs/global-config-guard-check.json` 与 `unity-guard-check.log`。本项目 `.codex/config.toml` 继续有效。
