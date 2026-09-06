# 当前状态

## 当前活动开发（v0.3）

用户已将范围更新为永久营地多人玩法雏形，活动任务见 `tasks/DemoPrototype_v0.3.md`，分支 `feat/demo-persistent-v03`。A—C 内部检查点已通过，当前接续 D：成长、公共修复、车辆与进阶坑。以下 M0 测试/构建保留为历史基线，不代表 Demo 已全部通过。旧的“只做 M0”限制不再适用；无需逐项确认默认数值。

本轮尚非完整朋友可玩候选。A 已推送 `f59486c`、B 已推送 `0dfec73`。C 卡通矿工、木屋场景、击晕搜袋、嘴藏烟酒、原石加工和托管赌桌已实现；最新 109/109 EditMode、C 真实双进程和重开 213/213、B 完整采矿路线回归130/130、A资产回归50/50通过，Windows Demo构建成功。最终画面复看修正后的屋门、完整HUD和矿镐，原生菜单保存退出PID9644 exit0。D—F继续，真人/异机/异网尚未验证；详细失败、修复及最终证据见活动任务。

Steamworks 官方页面目前未登录，项目内未找到自有 AppID。已集中给出本人登录操作；未付款、未注册发布、未改系统网络。其他开发继续。

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
