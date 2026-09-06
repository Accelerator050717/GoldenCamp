# GoldCamp 美术样件 01

同一 v0.3 工程的第一组视觉替换。参考用户提供的绿色豆形矿工三视图，营地与道具图；扩展图仅补充配件与表情。地图范围、角色能力、物品 ID 和存档协议没有因此增加。

`GoldCampSample01.blend` 是可编辑源：每个资产独立 collection，衣片、手指、铆钉、木板等保留分件；默认只显示矿工，其他 collection 可在 Outliner 开启。色彩图集打包在 blend 内。`miner.py`、`props.py`、`corner.py` 是可反复修改的生成源，`model_utils.py` 管理共享材料与几何。

使用官方免费 Blender 5.2 LTS。首次实跑版本 5.2.1；便携工具、安装下载与缓存不提交。

```powershell
cd M:\GoldCamp
pwsh -File Tools/Export-ArtSample.ps1
pwsh -File Tools/Invoke-Unity.ps1 -Action DemoGenerate
pwsh -File Tools/Invoke-Unity.ps1 -Action ArtPreview -WithGraphics
pwsh -File Tools/Invoke-Unity.ps1 -Action EditModeTests
pwsh -File Tools/Invoke-Unity.ps1 -Action DemoBuild
```

其他机器向 Export-ArtSample 传入 `-BlenderPath`。一次只运行一个导出或 Unity 任务。入口只重建自己命名的源与资产，测试/预览不读真人存档。

作者参数按 Unity 米制：Y 向上，Z 向前；Blender 源使用 Z 向上。导出克隆显式转换每层关节与网格基底，避免 FBX 根旋转补偿破坏关节。FBX 合并同一刚性关节下的静态部件，保留 19 个角色 Renderer，物品通常一个；全组使用四张 128×128 色彩图集（矿工四套配色），每个 Renderer 仅一个共享材质。面数见 `asset-manifest.json` 的实际导出统计。

Unity 资源位于 `Assets/GoldCamp/Demo/Art/Sample01`；`CampSampleImporter` 生成 Prefab 和 `Resources/GoldCampArtSample.asset`，`CampArtSet` 供现有视觉组件调用。角色仍由原有权威状态驱动刚性关节 IK，没有引入 Humanoid、根运动、职业或替代存档。

原石未开/开窗都不显示私有矿芯颜色，完全打开才按已经公开的矿芯选择外观。碰撞、触发器、目标组件仍由现有世界生成器维护，导入模型不携带这些组件。

预览 PNG 来自 Unity 实际 FBX 和游戏姿态代码；预览场景是隔离的姿态检查，不算真人或网络测试。实机第一人称画面、回归结果与制作/外观认可状态统一记录在 `docs/tasks/DemoPrototype_v0.3.md` 及美术交付记录。
