# 工程决定

## 当前 v0.3

- 用户最新明确任务取代 M0 阶段上限，连续实施活动任务 `tasks/DemoPrototype_v0.3.md`。复用 M0，不重建项目。
- 永久营地、世界内个人资产、有限坑永久开挖；默认继续存档。两处采区、短途四座车、基础语音、烟酒和托管赌桌不能默默删减。
- 继续单一 FishNet 候选，不同时安装第二网络框架。自有 Steam/AppID 尚无配置证据，先核验免费开发条件与实际账户权限，不擅自支付。
- 原四张主参考及新增两张辅助图已实际查看；采用可复现程序模型/程序动作，资产不得只用图片或全部胶囊白盒冒充。
- 功能分支 `feat/demo-persistent-v03`，验证后正常提交推送，不合并 main。
- FishNet 固定维护者 release `4.7.3`，Git commit `73f30cf2425dc808a4f463f0a233d386010810b3`；该提交 package.json 自报 `4.7.2`，以实际 lock 的 hash 为准。先用随包 Tugboat 验证真实本机/LAN 协议；Steam 传输和账户验证后接同一业务层。[维护者版本](https://github.com/FirstGearGames/FishNet/releases/tag/4.7.3)。
- 存档为完整世界快照，`world.camp.json` + `.bak1/.bak2`，SHA256 envelope、文件独占锁、临时文件 Flush 和原子主档替换。交易确认在完整快照落盘后发出；坏档/未来格式拒绝静默新建，测试目录与真人目录隔离。
- Steamworks 页面真实未登录，不能把无本地 AppID 推断成用户没有项目。新应用官方费用为每款 100 USD，超出本轮新增支出 0；仅查询现有权限，任何付款/合同/发布需本人处理。[官方应用费](https://partner.steamgames.com/doc/gettingstarted/appfee)。

## M0 历史决定（阶段范围已由上节更新）

1. 复用 Hub 现有任务安装 Unity 6.3 LTS `6000.3.23f1`（revision `09d2ecc7fb28`），现已安装并完成真实工程创建，不切换 6.6。工程版本锁定在 ProjectVersion.txt。[官方版本页](https://unity.com/releases/editor/whats-new/6000.3.23f1)。
2. 根目录 `M:\GoldCamp` 单仓库，工程子目录 `GoldCamp.Unity`。保留原始设计 Markdown，不复制长 GDD。
3. Windows x64 M0 采用 Mono Development 构建，已实际成功。WindowsStandaloneSupport 随编辑器安装；IL2CPP 所需额外 C++/SDK 工具链不作为 M0 前置要求。[官方要求](https://docs.unity3d.com/6000.3/Documentation/Manual/windows-requirements-and-compatibility.html)。
4. 不新增网络、Steam、账户、经济、存档、运行时 AI 和正式资产依赖。简单几何体与材质足够。
5. 唯一桥接固定 CoplayDev MCP for Unity `10.2.0`，UPM hash=`30d22075093d1d35dfb0091c1c7550e9ad948577`。真实 MCP 已完成 Console、对象查询、创建、保存验收，Editor API 另行通过。插件首次启动误写全局配置的副作用已经按用户授权清理，并加入项目会话保护；重新打开编辑器后全局哈希不变。配置、依赖和事故处理详见 AUTOMATION.md。
6. 原资料 ZIP、DOCX/PDF 和 intro 保留本地，源码 Git 使用原始 Markdown；M0 无需 LFS 大文件规则，Git LFS 已安装。
7. 测试与构建日志保存在被忽略的 `Builds` 下，文档记录实际结果摘要；不复制 Hub/许可原始日志入仓库。
8. 实际解析的包版本为 URP `17.3.0`、Unity Test Framework `1.6.0`；提交 manifest 与 packages-lock，依赖以这两份文件为准，不按模板猜版本。
9. 继续开发的唯一源码目录为 `GoldCamp.Unity/Assets/GoldCamp`；`.local/M0Staging` 是安装期间的历史准备材料，不再作为源码来源。已有工程不重复 CreateProject，场景和资源只通过项目 Editor API 生成。
10. 持有矿块保留原场景层级，在 LateUpdate 跟随持有点，避免父对象禁用时重挂载引发的 Unity 生命周期错误。释放前验证墙体与玩家胶囊空间；无安全位置时继续持有并显示提示。

## C 检查点补充

- 程序矿工/营地模型按参考制作，使用可重复Editor生成；Noto Sans CJK SC随游戏分发OFL通知，不依赖本机字体。嘴藏/烟酒/击晕/搜袋动作共享权威计时。
- 活动赌桌保存为零或一元素Wagers列表，避开Unity JsonUtility将null内联类写成空对象的问题；锁定原石和赌金同次提交，恢复后只结算一次，原石回各自安全柜。
- 普通掉落包以同一物理袋体承载多个独立ItemId；玩家胶囊与地面矿/袋不互相推出，E逐件取出。重矿仍是独立地面货。
- D默认按自愿分次贡献400游戏币+8铁开放公共旧车和进阶路，不自动分摊；个人矿镐180、探测器90，各自购买。参数集中配置，已生成矿坑的几何/内容与公共修复目标应随世界保存。
