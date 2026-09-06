# GoldCamp / 淘金营地

当前阶段：v0.3 永久营地多人玩法已形成局域网候选，活动分支 `feat/demo-persistent-v03`。包含采矿与梯子协作、个人交易与柜子、击晕抢夺/嘴藏/烟酒、原石赌桌、升级修车/短途运矿和基础语音。退出后继续同一世界；不是每局重开。分发与接续状态见 [STATUS](docs/STATUS.md) 和 [活动任务](docs/tasks/DemoPrototype_v0.3.md)。下方 M0 结果保留为历史基线。

当前193/193 EditMode、M0 PlayMode12/12；正常成长与车辆完整路线1335项、大档与诊断竞争200项通过。声音140、四本机身份含故障217、采矿130、社交含赌桌故障253项通过，构建边界见[验收记录](docs/VALIDATION_v0.3.md)。最终包实际原生Esc/工具/短步/跳跃、保存并继续同一营地通过。两设备LAN、异网双向语音、四名真人及中美组合、一小时试玩仍待实测；已观测偶发长帧，不能保证稳定60FPS。

Demo 的实际入口（从根目录执行，关闭本项目 Editor）：

```powershell
.\Tools\Invoke-Unity.ps1 -Action DemoGenerate -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action EditModeTests
.\Tools\Invoke-Unity.ps1 -Action PlayModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action DemoBuild -WithGraphics
.\Tools\Test-DemoNetwork.ps1
.\Tools\Test-DemoMining.ps1
.\Tools\Test-DemoSocial.ps1
.\Tools\Test-DemoProgression.ps1 -WithGraphics
```

E/F 实际入口：`Tools/Test-DemoVoice.ps1`、`Tools/Test-DemoFourPeers.ps1 -IncludeHostCrash`。四进程只代表本机自动网络检查，声音API与真人对话分别记录。`Tools/New-FriendCandidate.ps1` 核对源码及全部运行文件构建后哈希，在 `Builds/Candidates` 生成独立ZIP、清单和SHA256，不上传或发布；实际归档记录见STATUS。朋友说明见 [FRIEND_PLAYTEST](docs/FRIEND_PLAYTEST.md)。

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

打开已生成的 `Assets/GoldCamp/Demo/Scenes/Demo_Camp.unity`，点击 Play，再从菜单选择“继续上次营地”；也可独立新建或输入房主地址加入。WASD 移动、鼠标观察、Space 跳跃、E 交互/拾放、对梯子E开始或退出攀爬再W/S上下、Q 抛出、1/2 矿镐/探测器、左键使用工具、L 放梯、F 收入口袋、B 含/吐单颗金粒、X 取消动作、Tab 背包/烟酒、Esc 本地菜单。对准矿工时左键击晕、E 搜一个口袋格；房主可在菜单切换和平营地。加工台提供粗检/开窗/全开，对赌桌需双方确认后托管，结算原石回本人柜。菜单不暂停房间；返回营地会把随身货留在原地。完整真人试玩仍待测。

## M0 历史基线入口

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

当前Demo源码在 `GoldCamp.Unity/Assets/GoldCamp/Demo`：Core负责权威规则/存档，Networking负责会话/传输/模拟/语音，Presentation负责模型/UI/输入，Editor负责可重复生成，Tests保留规则回归。`Assets/GoldCamp` 下原Runtime、Editor、Tests属于M0历史基线。包版本以 `Packages/manifest.json` 与 `packages-lock.json` 为准。`.local/M0Staging` 是安装期间的历史暂存，不是继续开发的源码目录。

唯一桥接为 CoplayDev MCP for Unity `10.2.0`，提交固定为 `30d22075093d1d35dfb0091c1c7550e9ad948577`。已完成本项目实例的 17 步真实 MCP 烟测，Editor API 烟测也已独立通过；两者的证据分别列在 STATUS。项目内入口及配置样例见 [AUTOMATION](docs/AUTOMATION.md)，不需要修改全局 Codex/MCP 配置。

当前范围以 `Demo启动包_v0.3` 和活动任务为准，网络、存档、采矿、社交、成长与基础语音已进入当前候选，外部验收和已知问题见VALIDATION。环境与决定见 [ENVIRONMENT](docs/ENVIRONMENT.md)、[DECISIONS](docs/DECISIONS.md)。
