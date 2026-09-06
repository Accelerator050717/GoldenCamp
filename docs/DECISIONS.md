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

## D/E 当前实现决定

- 公共车固定4座、4个公开货位，只有驾驶座输入产生车运动；服务器扫描车壳和车轮下方支撑。退出或驾驶员离线停车，重开清理座位但保留车位与货物。维护台回收只移动空车，原货留原位置，不传送收益。
- 两坑各自保留PitId、保存的几何与固定产出；高级坑左列容许基础/耗尽工具挖石铁，其余硬层要求升级。暴露邻格与梯子支撑不能跨坑借用。
- 基础语音采用免费纯C# Opus，Concentus Unity `1.0.1`，固定提交 `f1564332c43ddf36f9129c5fca51d73c7b47d1fb`。[维护者源码](https://github.com/adrenak/concentus-unity/tree/f1564332c43ddf36f9129c5fca51d73c7b47d1fb)。无新增计费服务。
- 默认按住V发话，Unity主线程采集设备，工作线程编码16kHz单声道/20ms/24kbps；不同采集率使用连续Speex重采样。输入/输出队列有界，音频DSP回调按混音时钟消费PCM、连续插值到输出采样率并补零，不访问网络或执行编码。语音不写存档或音频文件。
- FishNet使用独立不可靠语音广播；服务器在GoldCamp身份加入后按真实连接绑定说话者、限速、过滤旧序号，仅转发20m内玩家。接收60ms预填、最多200ms包队列，500ms停流后丢旧缓冲；同流更大未消费序号可重新预填，避免走出范围再返回时永久失声。3m内正常音量，20m衰减截止，可选设备/本机和单人静音。
- 音效/环境/语音分组，基础反馈与环境音由缓存程序AudioClip生成；镜头摇晃默认关闭，显示和输入偏好独立于永久世界。测试档不读写真人偏好。
- 上述E为已接入源码的决定，Unity解析/测试/真实声音链仍须以活动任务记录为准。Steamworks/AppID与异网真人没有被这些本机实现替代。

- 永久营地大档状态按每个接收者先生成私有投影，再Gzip有界压缩/可靠分片；单片不超过既有128KiB协议预算，最大原文8MiB/压缩1MiB，收到全部有效片且校验通过才发布新View。普通操作请求仍保留原严格限制，不通过删减矿物或切换会话规避容量问题。实际失败/修正证据见活动任务。
- 性能记录区分Update帧间隔、FrameTimingManager最新CPU/GPU帧值和Unity图形资源分配估计；缺测以Available=false标记，驱动报告显存容量不当成实际显存占用。[Unity计时API](https://docs.unity3d.com/cn/6000.0/ScriptReference/FrameTimingManager.html)。

- 实际诊断证明流式AudioClip读取回调的预读导致PCM丢弃，因此语音改用OnAudioFilterRead混音块供数；近距离音量在主线程计算、音频线程应用，输出居中而非左右声像定位。合成音连续消费与丢弃比例另行验证，不等同真人听感。[Unity音频回调](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/MonoBehaviour.OnAudioFilterRead.html)。

- 会话锁定目标60fps，并把相同值传入FishNet的ClientManager/ServerManager，防止连接变化用默认最大帧率覆盖。会话关闭vSync以使软件上限生效；这是资源使用上限，不保证所有设备稳定60fps。
- 公共维护交互统一以实际维护台VehicleMaintenancePoint验证距离和视线，车辆驶离后仍可用；不以停车点代替台面。最终D实际台前贡献通过。
- 分发必须同时匹配Unity源码指纹及实际运行文件哈希，包含D3D12/WinPix等生成依赖；ZIP清单记录构建时和打包时各自提交，不回写历史构建提交号。真实身份与营地、测试档、缓存和源码调试备份不随包分发。
