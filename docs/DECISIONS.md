# M0 实际决定

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
