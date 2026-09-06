# GoldCamp / 淘金营地

当前阶段：在 M0 上持续实现 v0.3 永久营地多人雏形，活动分支 `feat/demo-persistent-v03`，进度见 [活动任务](docs/tasks/DemoPrototype_v0.3.md)。Demo 尚未交付；下方 M0 入口与 2/2 EditMode、12/12 PlayMode 结果保留为历史基线。真实证据见 [STATUS](docs/STATUS.md)。

当前 A 检查点已通过：47/47 EditMode、12/12 M0 PlayMode、50/50 真实双进程资产/保存重开检查。继续 B，不把当前资产开发界面当成完整试玩版。

Demo 的实际入口（从根目录执行，关闭本项目 Editor）：

```powershell
.\Tools\Invoke-Unity.ps1 -Action DemoGenerate -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action EditModeTests
.\Tools\Invoke-Unity.ps1 -Action PlayModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action DemoBuild -WithGraphics
.\Tools\Test-DemoNetwork.ps1
```

`DemoGenerate` 只重建 Demo 的生成场景并保留已有规则配置；不读写玩家存档。当前包 `Builds/DemoPrototype/Windows/GoldCamp.exe` 可进入继续/独立新建/地址加入菜单。默认端口 7770。实际玩家数据位于 Windows 的 `%USERPROFILE%\AppData\LocalLow\GoldCamp\GoldCamp`：`Identity/player.json` 是玩家身份，`Camps/<WorldId>` 是各自永久营地及两份备份。备份时先正常退出房主，再复制完整目录；不要分享身份文件。测试器仅使用独立 `.local/TestRuns` 标记目录。

- 根目录：`M:\GoldCamp`
- 工程：`M:\GoldCamp\GoldCamp.Unity`
- 编辑器：Unity 6.3 LTS `6000.3.23f1`；URP `17.3.0`；Unity Test Framework `1.6.0`
- 唯一远端：`https://github.com/Accelerator050717/GoldenCamp.git`（private）
- 设计来源：[原始 Markdown 提案](淘金营地_v0.1/淘金营地_完整开发提案_v0.1.md)

## 打开与试玩

在 Unity Hub 的 Projects 中添加已有目录 `M:\GoldCamp\GoldCamp.Unity`，选择 `6000.3.23f1` 打开。也可在确认该项目未被其他编辑器打开后运行：

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe' -projectPath 'M:\GoldCamp\GoldCamp.Unity'
```

打开已生成的 `Assets/GoldCamp/Scenes/M0_Graybox.unity`，点击 Play。代码中的按键为 WASD 移动、鼠标观察、Space 跳跃、E 拾取/放下、Q 抛出、R 重置、Esc 暂停并释放鼠标；暂停界面可恢复或退出。真实键鼠验收步骤见 [TEST_PLAN](docs/TEST_PLAN.md)。

## 生成、测试与构建

使用 PowerShell 7；运行前关闭本项目的交互编辑器，按顺序等待每条命令完成。脚本会拒绝重复占用项目，不操作其他项目编辑器。

```powershell
Set-Location -LiteralPath 'M:\GoldCamp'
.\Tools\Invoke-Unity.ps1 -Action Generate -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action SmokeAutomation -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action EditModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action PlayModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action Build -WithGraphics
```

Generate 会重建本项目灰盒场景并复用固定路径的资源；重新执行前，将需要保留的手工场景修改另存为其他场景或备份；只保存到原场景仍会被 Generate 覆盖。幂等验收时连续执行两次 Generate。日志和测试 XML 在 `Builds/Logs`，脚本返回本次路径；零测试不算通过。

Windows x64 Mono Development 构建已成功，运行 `M:\GoldCamp\Builds\M0\Windows\GoldCamp.exe`，必须保留相邻数据目录和运行库。最终构建已实际启动、检查日志并正常退出。鼠标、跳跃、暂停恢复和重置已有真实输入验证；持续移动与完整拾放/抛出流程仍待用户试玩。靠近黄色矿块并看向它，出现 E 提示后拾取；空手时按 R 可恢复初始状态。

`CreateProject` 仅用于从不存在的工程目录首次初始化；本工程已存在，不再运行。该动作需先独立验证许可，再传入 `-LicenseVerified`，该开关本身不检查或获取许可。

## 源码与自动化

实际源码为 `GoldCamp.Unity/Assets/GoldCamp` 下的 Runtime、Editor、Tests；包版本以 `Packages/manifest.json` 与 `packages-lock.json` 为准。`.local/M0Staging` 是安装期间的历史暂存，不是继续开发的源码目录。

唯一桥接为 CoplayDev MCP for Unity `10.2.0`，提交固定为 `30d22075093d1d35dfb0091c1c7550e9ad948577`。已完成本项目实例的 17 步真实 MCP 烟测，Editor API 烟测也已独立通过；两者的证据分别列在 STATUS。项目内入口及配置样例见 [AUTOMATION](docs/AUTOMATION.md)，不需要修改全局 Codex/MCP 配置。

当前范围以 `Demo启动包_v0.3` 和活动任务为准，网络、存档、采矿、社交、成长与语音正在连续实施。环境与决定见 [ENVIRONMENT](docs/ENVIRONMENT.md)、[DECISIONS](docs/DECISIONS.md)。
