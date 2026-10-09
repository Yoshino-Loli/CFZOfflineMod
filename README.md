# CFZOfflineMod

CrossFire Zero（穿越火线 Xenogenesis / CFZ）离线单机 Mod。

不需要登录服务器，本地即可进入游戏的 InGame 地图，与 AI 机器人对战。角色、武器、多套背包、灵敏度全部可在 `Config.ini` 中自定义。

> 仅供学习交流使用，本项目与 Smilegate / 官方无关，请支持正版游戏。

---

## 功能特性

| 类别 | 说明 |
|---|---|
| **离线进游戏** | 本地伪造 LobbyNetClient / GameClient / 登录事件链，绕过联网登录，直达模式选择界面 |
| **AI 对战** | F9 热键或模式选择界面的 **AI Combat** 按钮（原 AX Coming Soon 按钮）进入 InGame 地图 + AI 机器人 |
| **角色/武器自定义** | `Config.ini` 配置角色（SWAT/OMOH/SAS...）、队伍（BL/GR）、主/副/近战/投掷武器槽 |
| **多背包** | `背包2 ~ 背包7` 各配一套武器，B 键呼出面板数字键切换；死亡复活保持所选背包 |
| **开镜灵敏度修复** | 离线时 `Option.Mouse.FPS.Scope.Value` 恒 0 导致狙击开镜后完全转不动，进图自动补中性值（可 ini 覆盖） |
| **鼠标灵敏度** | 平时 / 开镜灵敏度均可 ini 配置（1~100） |
| **B 键背包修复** | 修复离线下背包弹窗"开了关不掉 / 关了又弹开"的死循环 |
| **声音/亮度/分辨率修复** | 进图后监听器补挂、音量恢复、亮度归位、分辨率兜底 |
| **卡加载看门狗** | Loading_End 3 秒兜底、卡 Creating 12 秒强推、掉出地图自动复活 |
| **登录提速** | 原"死等 30 秒推进"改为轮询，游戏推进到哪就立刻继续 |

调试（Debug 编译）额外提供：F1~F8 / F11 / F12 排查快捷键、按钮点击探针、游戏事件派发观察、地图状态观测快照、独立日志文件 `Latest.log`。

## 环境要求

- CrossFire Zero 客户端（`CFWClient.exe`，本 Mod 基于当前客户端反编译开发）
- [MelonLoader](https://github.com/LavaGang/MelonLoader)（游戏目录下已有 `MelonLoader\`）
- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer)（sinai-dev 版，`Mods\UnityExplorer.ML.Mono.dll`）——作为脚本宿主加载本 Mod
- Windows + .NET Framework 4.x（编译用系统自带 csc）

## 安装

游戏目录（`CFWClient.exe` 所在目录）下的最终结构：

```
<游戏根目录>\
  MelonLoader\            ← MelonLoader
  CFWClient_Data\Managed\ ← 游戏程序集 (编译时引用)
  Mods\
    UnityExplorer.ML.Mono.dll                  ← UnityExplorer
    sinai-dev-UnityExplorer\Scripts\startup.cs ← 加载脚本 (见下)
    CFZOfflineMod\
      CFZOfflineMod.dll   ← 本 Mod
      Config.ini          ← 配置 (首次运行自动生成默认)
      Helper.md           ← 角色/武器完整名单 (运行时自动生成)
```

`sinai-dev-UnityExplorer\Scripts\startup.cs` 内容（一行，随发布包附带）：

```csharp
System.Reflection.Assembly.LoadFrom(UnityEngine.Application.dataPath + "/../Mods/CFZOfflineMod/CFZOfflineMod.dll").GetType("CfzOffline.Boot").GetMethod("Run").Invoke(null, null)
```

## 构建

```powershell
# Release 编译 (默认): 只保留 F9/F10 快捷键, 部署到 ..\Mods\CFZOfflineMod\
.\build_mod.ps1

# Debug 编译: 全套调试快捷键 + 探针 + Latest.log
.\build_mod.ps1 -Config Debug

# 发布: Release 编译 + 组装 [dll + startup.cs + Config.ini + Helper.md] 到 publish\CFZOfflineMod\
.\build_mod.ps1 -Publish
```

源码为单文件 `cfz_mod.cs`（C# 5 / .NET 3.5 语法，与游戏 Mono 运行时兼容），编译引用游戏的 `CFWClient_Data\Managed` 与 `MelonLoader\net35\0Harmony.dll`，无需 Visual Studio。

## 配置（Config.ini）

与 dll 同目录，改完存盘按 **F10** 或下次进图（F9 / 按钮）自动重读。

| 段 | 内容 |
|---|---|
| `[按键]` | 全键位改绑（含 WASD 旁路）、鼠标灵敏度（1~100，空 = 最慢默认）、开镜灵敏度（空 = 自动补 50） |
| `[角色武器]` | 角色 / 队伍 / 四个武器槽（= 背包1）、背包2~背包7（逗号分隔武器名） |
| `[敌人]` | AI 数量、敌人角色/武器配置 |

全部可用角色 / 武器（含 weaponID）名单见 **Helper.md**——由 Mod 运行时从游戏数据表导出，按 F10 立即刷新。

## 快捷键

| 键 | 功能 | 版本 |
|---|---|---|
| F9 | 进 AI 地图（等同界面按钮） | 全部 |
| F10 | 重载 Config.ini | 全部 |
| F1~F8 / F11 / F12 / Shift+F10 | 调试工具（bundle 探测 / 强制推进 / 诊断 / 强关 Loading / 直进真实地图等） | 仅 Debug |

## 工作原理（简述）

UnityExplorer 编译执行 `startup.cs` → `Assembly.LoadFrom` 加载 dll → `CfzOffline.Boot.Run` 挂载 Harmony 补丁与常驻 Driver：

- **启动协程**：等待/伪造网络客户端实例，按事件链喂入登录流程，进入大厅
- **Harmony 补丁**：游戏事件观察、换背包离线回环、复活事件改写（保持背包）、UI 修复等
- **Driver（MonoBehaviour）**：每帧驱动——键位旁路（WASD/开火）、看门狗（卡加载/掉图/相机/声音）、模式界面按钮接管

## 已知限制

- 仅针对教学模式 AI 图；其他模式（真实地图 F7 / 单机教学 F8）为 Debug 调试入口，未经完整适配
- 游戏版本更新可能导致反编译结构变化，需要对照新程序集调整
- 巡逻兜底 / 可见性矩阵兜底 / 移动兜底等默认停用（历史上与其他系统打架，如需要见源码内注释恢复）

## 免责声明

本项目仅供学习研究与单机体验，不提供任何游戏客户端与官方资源，不用于商业用途，不对使用造成的任何后果负责。
