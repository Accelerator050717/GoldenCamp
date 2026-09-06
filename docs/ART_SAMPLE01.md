# 参考图驱动的美术样件 01

这是同一 `feat/demo-persistent-v03` 活动任务中的第一组样件，复用 v0.3 玩法、账号、物品 ID、永久营地和网络规则。**样件制作与用户外观认可分开：外观尚待用户集中评审，不据此批量扩展全部美术。**

集中看图：[实际模型与实机图库](art-sample01/index.html)。图片来自 Unity 实际导入模型/姿态代码或 Windows 游戏窗口，没有概念图充当实机。角色动作页是可复现姿态检查，不代表真人或跨网测试。

| 交付物 | 位置 / 内容 |
|---|---|
| 可编辑源 | `ArtSource/Sample01/GoldCampSample01.blend`，原始分件，图集已打包 |
| 可复现生成源 | `ArtSource/Sample01/miner.py`、`props.py`、`corner.py`、`model_utils.py`、`generate.py` |
| 导出入口 | `Tools/Export-ArtSample.ps1`，实际运行官方免费 Blender 5.2.1 LTS |
| Unity 资源 | `GoldCamp.Unity/Assets/GoldCamp/Demo/Art/Sample01` 的 20 个 FBX、Prefab 和四个共享材质 |
| 接入 | CampMinerModel、CampFirstPerson、CampWorld、CampArtBuilder；Resources/GoldCampArtSample.asset（CampArtSet），现有生成入口持续可用 |
| 统计 | 矿工 29,094 三角形、19 个刚性关节 Renderer；普通道具通常 1 个 Renderer，各 Renderer 一个材质；四套 128×128 图集。精确各资产统计见 asset-manifest.json |

实际读取了六张参考图及用途说明：01 三视图是角色主依据，05 补充配件和表情；02/04 决定营地与道具语言；03/06 只用于布局约束。四人共用绿色豆形矿工，帽/衣配色与小配件区分，没有人类矿工、职业属性或地图扩张。

这组包括矿灯帽、眼睛、短外套、手套与靴子；第一人称手、曲刃矿镐、掌上探测器；有显露矿脉的石/铁/金、未开原石、开窗、全开矿芯、捆扎重矿、分板木箱与绳结梯子；一号木屋、收购摊、道路边石、岩簇、分枝松树和灯光。其余营地资产保留现有版本，等待这一组风格反馈。

原石外观仅在全开后根据公开矿芯选择，富金与金共用金芯模型。既有 Collider/Rigidbody/CampTarget 仍由世界与生成器维护；FBX 不含玩法组件。手持矿展示改为匹配握点；攀爬时身体、手和矿共用梯子朝向，转头不改存档中的朝向。修正双手挥镐、烟嘴/瓶口位置并检查原车座姿态。

内部问题与修正证据保留：最初 FBX 自动转换破坏嵌套 EMPTY 的本地轴；改为导出克隆逐层转换矩阵和网格。原始球体 UV0 曾覆盖图集；现在只使用 UV0。独立矿面法线曾向内；开放色块保留明确朝外绕序。首次实际画面作为前后对照保存在隔离 VisualRuns，没有改原参考图。

本次最终资源对应验证如下，源码指纹 `6a6c31b8f122fda603a05efd94df175892506ac12d98e6462b3ed69baff960c8`（383 文件）：

| 层级 | 结果与证据（仓库根目录下） |
|---|---|
| 实际生成与导入 | `Builds/Logs/20260906-131155-DemoGenerate-73c0c742.log`，exit 0 |
| EditMode | **235/235**，失败/跳过 0；`Builds/Logs/20260906-131239-EditModeTests-18e8e17c.xml`。含真实 FBX/UV0/尺寸、48 相位双手握柄、嘴锚点、富金公开外观和房主/客户端攀爬转头检查 |
| Unity 模型截图 | `Builds/ArtReview/20260906-201329`，32 张原样归档；入口日志 `Builds/Logs/20260906-131317-ArtPreview-ec6371a1.log` |
| Windows 构建 | `Builds/Logs/20260906-131423-DemoBuild-af9dc50f.log`，exit 0；Unity 6000.3.23f1 |
| 采矿/梯子/协作/出售/重开 | **130/130**；`.local/TestRuns/20260906-201532-e2782a59fac543b99589c4e625dd3a9b/evidence.json`，四个先后进程 exit 0 |
| 社交/烟酒/嘴藏/原石/赌桌恢复 | **253/253**；`.local/TestRuns/20260906-201911-d15ceca38f33463594673c60d5ebba4f/evidence.json`。六个先后进程中一次明确注入房主故障 exit -1，其余 exit 0，无非预期强杀 |

这些是真实 Unity 和本机进程检查，非真人操作。M0 PlayMode 12/12、D 完整正常成长驾驶 1335 项、语音等为此前构建证据，详见 VALIDATION；本轮不将旧 D 全程或语音测试改写成新模型已重跑。最终实机与独立 ZIP 记录见 STATUS。旧 ZIP `GoldCamp-v0.3-20260906-172216-9a8af7f626b8.zip` 保留。

依然不把本机测试当成两设备 LAN、中美四人或真人听感；Steam/AppID、异网与真人体验的边界仍见 STATUS。

实机补验：`F-20260906-132113-db6662` 实际两进程矿工、驾驶座、短驾2.112m停稳/下车/原车货；`F-20260906-132630-98cd05` 原生2键显示掌上探测器，Esc菜单点击保存退出。三个进程均exit0，16项资产/日志/短驾检查通过，原世界ID、矿坑、梯子、余额/装备、柜与车货、解锁保持。该运行仅复制先前已通过的隔离D档，未使用真人档；没有用测试输入冒充真人。

实现已正常commit/push 604175d；独立候选 `GoldCamp-v0.3-20260906-203008-604175d37cdd.zip` 已归档，292个解压文件匹配。解压副本的新建/保存退出检查目前等待本人处理Windows网络权限弹窗，精确进程与接续见STATUS；同一运行文件的原构建实机/退出结果已通过。
