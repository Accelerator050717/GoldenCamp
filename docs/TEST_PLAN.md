# 测试与验收

## 当前 v0.3

以 `Demo启动包_v0.3/02_执行与验收.md` 的 A—F 与 T01—T18 为总验收，结果看 STATUS 和活动任务，不能凭脚本存在判断成功。A—D内部通过，E本机语音和F适用回归/原生输入已通过；分发与外部验收分别记录。运行中断后的精确断点保存在 `tasks/DemoPrototype_v0.3.md`。

从根目录执行 `Tools/Invoke-Unity.ps1 -Action DemoGenerate`、`EditModeTests`、受影响的 `PlayModeTests -WithGraphics`、`DemoBuild -WithGraphics`。Editor测试也使用独立标记根，所有玩家脚本只创建 `.local/TestRuns`；不使用真人存档。源代码或生成资源修改后重跑受影响验证。

| 入口 | 覆盖与证据边界 |
|---|---|
| `Export-ArtSample.ps1` → `DemoGenerate` → `ArtPreview -WithGraphics` | Blender 可编辑源、FBX 实际导入、Unity 原样截图。预览是隔离姿态场景，不能代替实机、网络或真人。美术专用 EditMode 检查真实几何/材质/本地轴和动作握点，外观由用户另行认可。 |
| `Test-DemoNetwork.ps1` | 实际双进程抢矿、出售/分账幂等、身份和私有投影、保存重开。 |
| `Test-DemoNetwork.ps1 -FullMineSnapshot -SnapshotReaderContention` | 明确的容量fixture：224格挖空、224梯、原始产物全保留；验证大投影通过真实网络和重开，另用真实读锁验证诊断阻塞不影响玩法，释放后恢复；不能冒充正常成长。 |
| `Test-DemoMining.ps1` | 正常输入路线、实际向下挖矿与梯子搬运、协作加入退出、出售转账和坑梯重开。 |
| `Test-DemoSocial.ps1 -CrashOnWagerLock` | 完整社交fixture，另在锁定提交后故意终止本脚本房主，验证原档一次结算和回放。预期故障与清理强杀分列。 |
| `Test-DemoProgression.ps1 -WithGraphics -ShowHostWindow` | 普通seed6营地，正常固定采格、卖矿升级、自愿修车、四座车驾驶/乘坐、进阶矿运货与重开；无fixture/注资/传送。ShowHostWindow用于实际画面/性能观察，玩法仍为自动轴输入。 |
| `Test-DemoVoice.ps1` | 双图形进程真网络、合成音与短暂真实麦克风、远近路由、持续同流续听、静音、DSP样本消费与丢弃比例；不保存录音，不等同真人听感。 |
| `Test-DemoFourPeers.ps1 -IncludeHostCrash` | 四个独立本机身份与最小fixture，四方竞争、迟加入/重连、原子交易和房主故障恢复。不是四个人或异网。 |
| `New-FriendCandidate.ps1` | 核对源码/构建指纹，完整依赖与许可证，生成独立ZIP、manifest和SHA256；不发布。 |

T01—T16/T18的权威规则、存储故障和投影测试在Demo EditMode；M0实际引擎物理保留PlayMode。脚本驱动、真实窗口、原生键鼠和真人体验分开记录。T17原生Esc已验证：菜单中本机W不移动，同房peer继续移动、世界时间继续；关闭菜单后本机短步恢复。两设备LAN、异网双向语音、四人中美和一小时真人体验必须独立留证。

性能区分隐藏图形实例、可见图形实例与headless。记录Update帧间隔、可用时的最新CPU/GPU帧计时、进程内存（Unity Mono缺测时用脚本取实际PID）、Unity图形资源分配估计。显存容量不是占用；不杜撰最低配置或稳定60FPS保证。

## M0 历史基线

测试源码位于 `GoldCamp.Unity/Assets/GoldCamp/Tests`。最新实际结果：**EditMode 2/2 通过，PlayMode 12/12 通过，均失败 0、跳过 0**；结果 XML 与自动化证据见 `docs/STATUS.md`。Windows 构建、启动、部分真实键鼠和正常退出已验证；完整人工操作待用户验收，具体边界见 STATUS。编辑器固定为 Unity `6000.3.23f1`。

## 执行顺序

使用 PowerShell 7，在 `M:\GoldCamp` 运行实际脚本 `Tools/Invoke-Unity.ps1`。每个命令结束后再运行下一个；已有本项目编辑器或工具任务正在执行时，不重复启动。已存在的工程从 Generate 开始。

```powershell
Set-Location -LiteralPath 'M:\GoldCamp'
.\Tools\Invoke-Unity.ps1 -Action Generate -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action Generate -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action SmokeAutomation -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action EditModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action PlayModeTests -WithGraphics
.\Tools\Invoke-Unity.ps1 -Action Build -WithGraphics
```

- `CreateProject` 只供首次创建缺失工程使用；当前 `GoldCamp.Unity` 已存在，不再运行该动作。确需从空目录创建时，先独立验证安装与许可，再执行 `.\Tools\Invoke-Unity.ps1 -Action CreateProject -LicenseVerified`。该开关表示许可已经另行验证，本身不会获取或检查许可证；其他动作无需此开关。脚本拒绝覆盖任何已有工程目录。
- Generate 连续运行两次，检查场景、资源与构建场景设置不重复；应始终只有一个玩家、一个主相机、一个可拾取矿块。
- SmokeAutomation 应实际查询场景对象、创建或修改测试对象、保存并重新读取结果，检查本次 Console/编辑器日志。只生成配置或能启动进程不算自动化连通。
- MCP 连接成功时，另记录真实 Console 读取、对象查询、修改和保存的工具结果；采用 Editor API 替代时如实记录，不能把替代入口标为 MCP 成功。
- 每次调用的编辑器日志保存在 `Builds/Logs/<时间>-<Action>-<运行标识>.log`；测试另生成同名 `.xml`。以脚本返回的 `LogPath`、`Tests.XmlPath` 为本次证据，不能引用旧文件。Player 日志另记录实际采集路径。

## 自动化覆盖

以下方法均已纳入上方实际测试结果。EditMode 为两个场景配置测试；归属、物理和交互在 PlayMode 中执行。

`EditMode/M0SceneTests.cs`：

| 测试方法 | 实际检查 |
| --- | --- |
| `GeneratedSceneIsAnEnabledBuildSceneExactlyOnce` | 灰盒场景资产存在；Build Settings 中恰好一条启用项；配置了 URP 资产。 |
| `GeneratedSceneHasOnePlayerCameraOreAndCompleteReferences` | 地面、光源、玩家、矿块唯一；主相机、持有点、ResetOre 与刚体/碰撞引用完整，无 Missing Script。 |

`PlayMode/OreInteractionTests.cs`：

| 测试方法 | 实际检查 |
| --- | --- |
| `PickupMaintainsOneOwnerAndStableHeldPhysics` | 成功拾取后两端归属一致；重复拾取、另一持有者抢取和同一持有者再拿另一块均失败；移动/转动持有点后矿块仍稳定跟随，刚体为 kinematic、碰撞关闭。 |
| `DropClearsBothOwnersAndRestoresGravityAndCollision` | 放下清除两端归属，恢复动态刚体、重力和碰撞；重复放下失败；经过物理步矿块实际下落。 |
| `ThrowReleasesOnceAndMovesForward` | 抛出解除归属并恢复物理，产生向前速度与位移；重复抛出不叠加速度。 |
| `InvalidTargetsCannotBePickedUpThroughDistanceOrWalls` | 空目标、超距、墙壁遮挡、未激活、靠近但不在准星射线上的矿块均不能拾取；射线命中表面但中心超距时不显示拾取提示，移近后才聚焦并允许拾取。 |
| `BlockedDropAndThrowPreserveTheHeldOre` | 完全无释放空间时放下/抛出失败并保留归属；移除障碍后可释放。 |
| `CloseWallReleaseNeverEmbedsOreInWallOrPlayer` | 贴近墙壁放下时，成功则验证矿块未穿入墙或玩家；无法安全释放则保留持有。 |
| `PauseBlocksPickupDropAndThrowUntilResume` | 通过 `SetPaused` 暂停后拾取、放下、抛出被拒绝，恢复后可交互。 |
| `PlayerResetClearsHeldOwnershipAndReturnsOreToSpawn` | 调用 `ResetPlayer` 恢复玩家及所持矿块初始位置，清除归属与速度，矿块可再次拾取。 |
| `PlayerResetAlsoRecoversAFreeThrownOre` | 抛出后调用 `ResetPlayer` 仍能恢复矿块位置与物理状态。 |
| `DisablingHolderRestoresOreAndAllowsPickupAfterReenable` | 持有时禁用玩家，清除两端归属，矿块保持独立激活并恢复出生位置和物理；重新启用玩家后可再次拾取，无意外错误。 |
| `OutOfBoundsFreeOreReturnsToSpawnWithNormalPhysics` | 空闲矿块越过 RescueHeight 后自动恢复出生位置和正常物理，去除原水平速度与旋转。 |

`PlayMode/M0ScenePlayTests.cs` 的 `ActualBuildSceneLoadsWithRequiredRuntimeReferences` 通过 Build Settings 实际加载灰盒，隔离备份场景的物理，检查关键组件与引用；以正常帧时长等待 `Update` 中的玩家移动，严格确认 grounded 状态及脚底贴近实际地面。

这些测试直接调用运行时 API，并非模拟真实键鼠。WASD、鼠标观察、跳跃、暂停菜单、R 键以及玩家自身越界恢复按下方人工步骤验收。单进程中的归属测试不代表多人同步或房主裁决已实现。墙角、朝地面释放及提示可读性也需实际试玩补验。

## 结果判定

1. 静态检查单独记录：代码/配置检查、脚本参数检查、`git diff --check`。静态通过不代表 Unity 已编译或运行。
2. EditMode、PlayMode 分别记录 XML 中的实际总数、通过、失败、跳过/忽略数与日志路径；退出码成功但零用例、结果缺失或只有跳过，均不得报告测试通过。
3. 编译及用例失败必须修复并重跑受影响验证。最后一次代码、场景、资源或构建设置修改后，以新一轮结果为准。
4. Build 必须由目标编辑器实际构建成功，产物为 `M:\GoldCamp\Builds\M0\Windows\GoldCamp.exe`，保留旁边所需的数据目录和运行库。存在旧 exe 不算本次构建成功。
5. 实际启动构建并检查属于本项目的窗口、画面与 Player 日志；退出后确认本项目进程结束。出现异常、缺失引用或阻塞错误需排查。
6. 仅在适用测试和构建通过后按授权提交/推送；只暂存明确路径。启动与真实输入验收仍独立记录，不能由构建结果代替。

## 最短人工试玩

代码中的键位：WASD 移动，鼠标观察，Space 跳跃，E 拾取/放下，Q 抛出，R 重置，Esc 暂停并释放鼠标。界面可恢复游戏或退出；键位是否实际生效需真实输入验收。

打开完整构建目录中的 `GoldCamp.exe`：

1. 确认灰盒场景正常显示、黄色矿块可辨认、操作提示可读。用 WASD 和鼠标移动观察，向地面及墙壁行走不穿透；Space 跳跃后能落地。
2. 从远处、隔墙和未瞄准时尝试 E，均不能拾取；接近并看向矿块才出现提示并允许拾取。
3. E 拾取后连续走动、转身和跳跃：矿块稳定，玩家不被弹飞。E 放下后矿块受重力落下，可以重新拾取；连续快速按 E 不出现复制或丢失。
4. 再次拾取后 Q 抛出：矿块离手向前运动并落地，可再次拾取。贴墙、低头对地、站墙角重复放下和抛出，不能嵌入场景或把玩家推出去；无空间时明确提示。
5. 持有时按 Esc：鼠标可自由移动，游戏不会因菜单操作继续走动或释放矿块；按界面指示恢复后可继续操作。
6. 分别在空手和持有时按 R，确认玩家与矿块恢复可玩的初始状态。矿块越界恢复有自动用例；玩家越界恢复尚需在可达测试条件下人工补验，不得把矿块用例视作玩家用例。
7. 通过暂停界面的退出功能关闭游戏，确认窗口与对应进程退出。

分别记录“自动测试”“构建”“启动/画面”“真实键鼠操作”和“用户试玩”的完成情况。若仅验证构建与启动，交付标注：**构建及启动已验证，完整人工操作待用户验收**。

## 记录格式

每轮在 STATUS 中记录：验证时间、编辑器完整版本、代码/资源对应状态、各入口执行结果、实际用例统计、构建与日志路径、启动观察、真实输入覆盖、剩余问题。尚未运行写“未运行”，不可填写预期成功结果。

以上灰盒细项为 M0 历史基线。当前轮次必须继续执行本文件开头的 v0.3 总验收，网络、资产、存档、采矿、社交、成长与基础语音均在当前范围；M0或双实例抢矿不能作为本轮终点。
