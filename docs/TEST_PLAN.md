# M0 测试与验收

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

本轮不测试网络、Steam、经济、存档、采矿或正式美术。M1 最小接续仅为两个本地实例争抢同一矿块且仅一个成功，本轮不实现。
