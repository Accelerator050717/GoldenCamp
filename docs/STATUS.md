# 当前状态

## 当前活动开发（v0.3）

当前任务 `tasks/DemoPrototype_v0.3.md`，分支 `feat/demo-persistent-v03`；永久营地范围覆盖A—F，旧“仅M0”上限已失效。A—C已推送f59486c、0dfec73、b4ef0ae。最终094420构建的D正常采矿筹资/升级/修车/驾驶乘坐/进阶采矿运货/司机重连/全房重开1335/1335通过，5个先后进程均正常退出；不是真人操作或异网证明。

当前源码193/193 EditMode（094312）、Windows094420构建exit0，M0 PlayMode12/12保留。大档/诊断并发读锁实际200项通过：临时锁住测试用view.json不阻止玩法或peer更新，解锁后诊断恢复、日志无逸出异常。帧率/音频等先前已通过声音140、四本机身份故障217、B130、C253；最后修改仅测试诊断导出。

D最终证据 `.local/TestRuns/20260906-164624-93e37e712e9b4a15947d4e67d929aa28/evidence.json`。上一879项失败、诊断IO问题及修复均保留历史记录。源码1eff2c83f1c5ce4ef8726d1e0c4bf5546cab32cfa2d000d50f36c21926665bb4，build-info绑定290个运行文件。

原生UI最终检查 `.local/VisualRuns/F-20260906-101250-59851f`：Esc只阻止本地输入，peer移动且世界时间推进；关闭后短步、1/2切换和跳跃正常。实际点击保存结束与继续，世界/身份、余额157、37已挖格、梯子/工具/柜子、车及1件车货保持；两进程exit0。无真人存档被使用。性能均值58.44/59.85FPS，最差帧776.862/602.378ms，偶发长帧尚未定位，详见VALIDATION。接着完成源码提交、ZIP解压启动与分发记录；不存在继续运行的测试进程。
Steamworks页面尚未登录、项目内未找到自有AppID；已集中给出本人操作，未收到完成回复，未付款/签约/发布或改系统网络。两设备LAN、异网、四人中美及一小时真人体验均待真实参与；本机进程不能代替。

以下为 M0 历史核验（2026-09-05 America/Los_Angeles）：工程、自动化、自动测试、Windows 构建和实际启动已验证。完整人工操作待用户验收；不将启动与 API 测试等同于全部交互通过。

## 已核验

- 唯一工程根目录 `M:\GoldCamp`；Unity 工程 `M:\GoldCamp\GoldCamp.Unity`。原资料目录与 `intro` 保留，源码位于实际工程 `Assets/GoldCamp`，`.local/M0Staging` 仅为历史暂存。
- Unity 6.3 LTS `6000.3.23f1` 已安装，ProductVersion=`6000.3.23f1_09d2ecc7fb28`；真实 Editor 创建工程退出 0，许可可用。URP `17.3.0`、Test Framework `1.6.0` 已解析并锁定。
- Visual Studio Community 2026 `18.9.12120.119` 已安装；没有改用 Unity 6.6，没有安装 Web Build Support、离线 Documentation 或 Get Started 教程。完整环境见 ENVIRONMENT。
- 灰盒场景、玩家移动/观察/跳跃、矿块拾取/放下/抛出、交互提示、暂停与重置源码已实现。持有矿块保留独立层级，禁用持有者能清除归属并恢复矿块；墙边释放会检查玩家与场景的安全空间。
- 项目 Editor API 已实际生成场景并完成重复生成与烟测；CoplayDev MCP for Unity `10.2.0` 已完成指定项目实例的真实连通烟测。未修改系统 PATH。插件首次 GUI 启动误写全局配置的副作用已按用户授权备份、清理，并在重新启动时验证保护有效，详见 AUTOMATION。
- Git 根为 `M:\GoldCamp`，唯一目标 `https://github.com/Accelerator050717/GoldenCamp.git`，private；代码提交已推送，结果见下方交付记录。

## 实际验证结果

| 验证层级 | 实际结果 | 本次证据（相对根目录） |
| --- | --- | --- |
| 工程创建 | Editor 批处理退出 0 | `Builds/Logs/20260905-184237-CreateProject-c8b8edfb.log` |
| 场景生成 | 已成功重复生成灰盒 | `Builds/Logs/20260905-184654-Generate-4d3f8bdc.log` |
| Editor API 烟测 | 查询、修改、保存、重开成功 | `Builds/Logs/20260905-184704-SmokeAutomation-c0ebcbf1.log`、`Builds/Logs/editor-automation.txt` |
| MCP 烟测 | 17 步通过；读取 Console、查询场景、创建/检查对象并保存验证 | `Builds/Logs/unity-bridge-probe-20260905-184825-smoke-1b9719ed.json` |
| EditMode | **2/2 通过，失败 0，跳过 0** | `Builds/Logs/20260905-185346-EditModeTests-fef72d5c.xml` |
| PlayMode | **12/12 通过，失败 0，跳过 0**；含持有者禁用、墙边释放、提示与中心距离一致性回归 | `Builds/Logs/20260905-190545-PlayModeTests-fe0c4216.xml` |

此前编译和 PlayMode 失败已修正；上表测试使用修正后的实际结果。场景落地测试隔离背景场景物理，按 `Update` 等待玩家落地并检查脚底与地面位置。测试直接调用运行时 API，不能替代真实键鼠试玩；MCP 烟测也不等于游戏交互验收。

## 本轮交付

| 项目 | 当前状态 |
| --- | --- |
| Windows x64 Mono Development 构建 | 成功，Unity 退出 0；报告 160,636,557 bytes。`Builds/M0/Windows/GoldCamp.exe` 及 UnityPlayer.dll、GoldCamp_Data、MonoBleedingEdge 等完整配套存在。日志：`Builds/Logs/20260905-190603-Build-548bb4ea.log`。 |
| 实际启动与退出 | 最终构建真实启动，灰盒、黄色矿块和提示显示正常；通过 Quit game 退出，进程 34192 退出码 0。日志：`Builds/Logs/player-final-smoke.log`。 |
| 真实键鼠操作与用户试玩 | 最终构建已实测鼠标观察、Space 跳跃及落地、R 重置、Esc 暂停、Resume 和 Quit game。工具只能短按方向键，未完成持续 WASD 走动及 E 拾放、Q 抛出的完整真实输入流程；这些 API 行为有自动测试，用户仍需按 TEST_PLAN 试玩。 |
| Git commit / push | `main`；M0 代码提交 `ee0e82672ebd04e1afd64ad04408254a72214082` 已正常推送至指定 origin，推送后远端 main 与本地 HEAD 一致。其后本文件的交付记录单独作文档提交。 |

Player 日志未发现脚本异常、缺失引用或加载失败。存在 DX12 info queue 和刷新率/vsync 诊断消息，Unity 使用 CPU 时间计时后画面可正常运行；未将其记为零警告或性能验收通过。构建过程生成的临时 `Assets/Resources/PerformanceTestRunInfo.json` 已由 Unity 自动清理。

## 当前阻塞与接续

M0 当时无安装、许可、编译、自动测试、构建或推送阻塞。当前 Demo 继续实现中的内容及外部缺口以活动任务为准，最终代码或场景有改动时重跑受影响验证。

双实例争抢矿块现在是 A 的内部闸门；总任务还包含矿坑、社交、成长、语音和外部验收，不在 A 结束。
