# 本机环境

核验日期：2026-09-05（America/Los_Angeles）。安装项已补充为安装完成后的实际状态；磁盘容量等初检数据保留为快照。

| 项目 | 实际结果 |
|---|---|
| Windows | Windows 11 家庭中文版 x64，build 26200 |
| 终端 | PowerShell 7.6.5 |
| CPU | Intel Core i9-13980HX，24 核 / 32 线程 |
| 内存 | 可用物理总量 15.63 GiB |
| GPU | NVIDIA RTX 4070 Laptop，8188 MiB VRAM，driver 595.97；Intel UHD |
| 磁盘剩余 | C: 39.06 GiB；M: 236.72 GiB（安装前初检快照，非当前余量） |
| Git / LFS | 2.44.0.windows.1 / 3.4.1 |
| Git 远端 | `https://github.com/Accelerator050717/GoldenCamp.git`，初检为 private 空仓库，现有身份有 push/pull 权限；提交/推送当前状态见 STATUS |
| Unity Hub | 3.21.1，`M:\unity_hub\Unity Hub\Unity Hub.exe` |
| Unity Editor | 已安装：`C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe`；文件 ProductVersion=`6000.3.23f1_09d2ecc7fb28` |
| Windows 构建支持 | Editor 的 `Data/PlaybackEngines/windowsstandalonesupport` 目录已存在；Windows x64 Mono Development 构建实际成功 |
| 安装范围 | 复用 Hub 的编辑器与 Visual Studio Community 2026 安装任务；未安装 Web Build Support、离线 Documentation、Get Started 教程 |
| 授权与工程创建 | Unity Personal 已分配；真实 Editor 的 CreateProject 批处理退出 0，生成工程，验证本次编辑器许可可用；日志见下方 |
| URP / 测试包 | `com.unity.render-pipelines.universal`=`17.3.0`；`com.unity.test-framework`=`1.6.0`，已写入实际 manifest 与 lock |
| 代码工具 | VS Code 1.135.0，`M:\vscode\Microsoft VS Code\bin\code.cmd` |
| Visual Studio | Community 2026 `18.9.12120.119` 已安装：`C:\Program Files\Microsoft Visual Studio\18\Community`，devenv.exe 已存在 |
| .NET / Windows SDK | 实际 `.NET SDK 10.0.400` 位于 `C:\Program Files\dotnet\sdk`；Windows SDK 未单独核验；本轮 Mono 构建不以 IL2CPP 工具链为前置 |
| Python / uv | Python 3.10.1；桥接所需 uv 0.12.10 已装到 `.local/python-tools`，未加系统 PATH |
| Unity MCP | UPM 与 Python server 均固定 `10.2.0`；UPM hash=`30d22075093d1d35dfb0091c1c7550e9ad948577`；真实协议连接及场景操作已验证，详见 STATUS |
| 本机工具 | PowerShell、文件读写、Git、Windows computer-use、浏览器工具可用；插件首次启动的全局配置副作用已处理，保护复验通过，详见 AUTOMATION |

## 版本来源

- [Unity 6000.3.23f1](https://unity.com/releases/editor/whats-new/6000.3.23f1)，2026-08-26，revision `09d2ecc7fb28`。
- 下载来自 `download.unity3d.com`，复用 Hub 现有任务。
- [Unity Windows 系统要求](https://docs.unity3d.com/6000.3/Documentation/Manual/windows-requirements-and-compatibility.html)。

真实工程创建日志：`Builds/Logs/20260905-184237-CreateProject-c8b8edfb.log`，CreateProject 退出码 0。首次导入遇到的 `Camera.AddComponent` 编译错误已修改为 `camera.gameObject.AddComponent`，修正后的编译结果由 STATUS 记录；此处不把源码修正视为编译通过。

实际工程为 `M:\GoldCamp\GoldCamp.Unity`；运行时、Editor API 与测试位于 `Assets/GoldCamp`。安装前的 `.local/M0Staging` 仅为历史暂存。包解析成功、工程创建成功与测试/构建成功是不同证据，后者以 STATUS 为准。
