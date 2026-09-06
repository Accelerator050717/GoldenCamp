# GoldCamp / 淘金营地

- 唯一工程根目录：`M:\GoldCamp`。Unity 工程：`GoldCamp.Unity`。产品名 GoldCamp，远端仓库名 GoldenCamp，均保留。
- 开始先读 `docs/STATUS.md`、`docs/DECISIONS.md`、`docs/TEST_PLAN.md`；长设计引用 `淘金营地_v0.1/淘金营地_完整开发提案_v0.1.md`。
- 当前仅 M0：Unity 6.3 LTS、URP、单人第一人称移动与拾放/抛出矿块、编辑器自动化、测试及 Windows 构建。网络、Steam、经济、存档、采矿和正式美术不在本轮。
- 锁定编辑器 `6000.3.23f1`，不因 Hub 推荐改变版本。安装/授权成功后才创建工程。包版本以实际生成的 manifest 和 lock 文件为准。
- 保留原资料目录与 `intro`，不覆盖未知文件；不改全局 Codex/MCP 配置。同一时刻仅一个写入者修改场景、Prefab 或包清单。
- 用 Editor API 生成场景与资源，生成入口必须可重复执行。持有矿块只有一个归属，释放后恢复物理，玩家和持有矿块不相互推挤。
- Git 根是本目录，不建嵌套仓库。唯一 origin 为 `https://github.com/Accelerator050717/GoldenCamp.git`，保持 private。禁用 force push、hard reset、清理用户文件；按精确路径暂存。
- 用户已授权本轮适用测试和构建通过后的正常 commit/push；此前不提交未验证工程。保留 `.meta`，不提交缓存、构建、许可证、凭证和机器专属设置。
- 必须区别静态检查、实际测试、构建、启动及真实输入。零测试不算通过；最后代码修改后重跑受影响验证。
- 仅操作本项目 Unity/游戏窗口与进程。真实登录、许可确认和系统权限由用户完成，给最少操作，不重复轮询下载。
- 当前脚本与验证状态查 README 和 STATUS；不存在/未运行的入口不得声称成功。
