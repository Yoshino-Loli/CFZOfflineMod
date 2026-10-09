// ============================================================
//  CFZ Offline Mod - 标准类库版 (游戏外 csc 编译)
//  1. Harmony 补丁: 缺失 ProudNetClientPlugin.dll 时网络层不再崩溃
//  2. 修复 LobbyNetClient 初始化 (重跑 Start)
//  3. 伪造连接状态, 自动驱动登录链直到模式选择界面
// ============================================================
using System;
using System.Collections;
using System.Collections.Generic;      // ★ 难度 Transpiler 要用 IEnumerable<CodeInstruction>
using System.Reflection;
using System.Reflection.Emit;          // ★ 难度 Transpiler 要用 OpCodes
using HarmonyLib;
using UnityEngine;
// ★ 别名: [角色武器] 那套配置用 (文件里其它地方一律用全限定名, 加别名不影响它们)
using CharacterType = CFW.Shared.Enum.CharacterType;
using ETeamID = CFW.Shared.Enum.ETeamID;
using WEAPONSLOT = CFW.Shared.Enum.WEAPONSLOT;
using DefinedWeaponID = CFW.Shared.Enum.DefinedWeaponID;

namespace CfzOffline
{
    public static class Boot
    {
        public static void Run()
        {
#if DEBUG
            // ★Debug 版日志落盘★ 把所有 [CFZ-Offline] 开头的日志额外写进 dll 同目录的 Latest.log
            //   (每次启动清空重写, 只保留最新一次运行的 mod 日志; Release 版不写)
            try
            {
                string loc = null;
                try { loc = Assembly.GetExecutingAssembly().Location; } catch { }
                string d = string.IsNullOrEmpty(loc) ? "." : System.IO.Path.GetDirectoryName(loc);
                _latestLog = System.IO.Path.Combine(d, "Latest.log");
                System.IO.File.WriteAllText(_latestLog,
                    "CFZOfflineMod (Debug) 运行日志 — " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                    "说明: 只收录 [CFZ-Offline] 开头的 mod 日志; 引擎完整日志在 output_log.txt\r\n\r\n");
                Application.logMessageReceived += OnLatestLog;
            }
            catch { }
#endif
            ModConfig.EnsureAndLoad();       // ★ 读取 Mods\CFZOfflineMod\Config.ini (键位表, 不存在则生成默认)
            Loadout.Load();                  // ★ 读取 [角色武器] 段 (数据表还没好就先记着, 进图/按 F10 自动重试)
            var go = new GameObject("CFZ-Offline-Driver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CfzOfflineDriver>();
            Debug.Log("[CFZ-Offline] Driver 已挂载 (" +
#if DEBUG
                      "Debug"
#else
                      "Release"
#endif
                      + " 编译)");
        }

#if DEBUG
        static string _latestLog = null;

        static void OnLatestLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (_latestLog != null && !string.IsNullOrEmpty(condition) &&
                    condition.IndexOf("[CFZ-Offline]", StringComparison.Ordinal) >= 0)
                    System.IO.File.AppendAllText(_latestLog, condition + "\r\n");
            }
            catch { }
        }
#endif
    }

    public static class Patcher
    {
        public static bool Noop() { return false; }

        static bool installed = false;

        public static void Install()
        {
            if (installed) return;
            installed = true;

            var harmony = new Harmony("com.cfz.offline");
            var noob = new HarmonyMethod(typeof(Patcher).GetMethod("Noop",
                BindingFlags.Public | BindingFlags.Static));
            var ALL = BindingFlags.Public | BindingFlags.NonPublic |
                      BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            // ---- 1) ProudNet 托管层全部 no-op ----
            int n = 0;
            foreach (var t in typeof(Nettention.Proud.NetClient).Assembly.GetTypes())
            {
                if (t.IsEnum || t.IsInterface) continue;
                foreach (var m in t.GetMethods(ALL))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    try { harmony.Patch(m, prefix: noob); n++; } catch { }
                }
                foreach (var c in t.GetConstructors(ALL))
                {
                    try { harmony.Patch(c, prefix: noob); n++; } catch { }
                }
            }
            Debug.Log("[CFZ-Offline] ProudNet no-op: " + n);

            // ---- 2) 生成的 Proxy/Stub 派生类全部方法 no-op ----
            int g = 0;
            foreach (var t in typeof(CFW.Network.LobbyNetClient).Assembly.GetTypes())
            {
                var bt = t.BaseType; bool isGen = false;
                while (bt != null)
                {
                    if (Equals(bt, typeof(Nettention.Proud.RmiProxy)) ||
                        Equals(bt, typeof(Nettention.Proud.RmiStub))) { isGen = true; break; }
                    bt = bt.BaseType;
                }
                if (!isGen) continue;
                foreach (var m in t.GetMethods(ALL))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    try { harmony.Patch(m, prefix: noob); g++; } catch { }
                }
                foreach (var c in t.GetConstructors(ALL))
                {
                    try { harmony.Patch(c, prefix: noob); g++; } catch { }
                }
            }
            Debug.Log("[CFZ-Offline] Proxy/Stub no-op: " + g);

            // ---- 3) NetConnector: 禁止真实连接与重连 ----
            foreach (var m in typeof(NetConnector).GetMethods(ALL))
            {
                if (m.Name == "OnConnectToHost" || m.Name == "ReConnectToHost")
                {
                    try { harmony.Patch(m, prefix: noob); Debug.Log("[CFZ-Offline] 已拦截 " + m.Name); } catch { }
                }
            }

            // ---- 4) Option.ApplyResolution: 强制按屏幕原生分辨率修正 (修复 800x600 锁死) ----
            try
            {
                var resPf = new HarmonyMethod(typeof(Patcher).GetMethod("ApplyResolutionPrefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                var m = AccessTools.Method(typeof(Common.System.Option.Option), "ApplyResolution");
                if (m != null)
                {
                    harmony.Patch(m, prefix: resPf);
                    Debug.Log("[CFZ-Offline] 已 hook Option.ApplyResolution");
                }
                // ★★★ 进游戏后走的是 ApplyResolution_InGame! ★★★
                //   CommonOption.ApplyFPS (CommonOption.cs:496-507):
                //     if (CurrentState == InGame || Tutorial) Option.ApplyResolution_InGame();   ← 进图走这条
                //     else                                     Option.ApplyResolution();
                //   Option.cs:358  ApplyResolution_InGame → Screen.SetResolution(Graphic.Resolution_InGame.Width, ...)
                //   ★ Resolution_InGame 与 Resolution 是两个独立字段 (Graphic.cs:8-10)
                //     → 只 hook ApplyResolution 的话, 一进图就被 Resolution_InGame(800x600) 覆盖
                var m2 = AccessTools.Method(typeof(Common.System.Option.Option), "ApplyResolution_InGame");
                if (m2 != null)
                {
                    harmony.Patch(m2, prefix: resPf);
                    Debug.Log("[CFZ-Offline] 已 hook Option.ApplyResolution_InGame (进图分辨率)");
                }
                else Debug.LogWarning("[CFZ-Offline] Option.ApplyResolution_InGame 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook ApplyResolution 失败: " + e); }

            // ---- 4b) ★音频 / 弹孔 探针★ ----
            // 只装探针, 真正的修复在 CfzOfflineDriver.EnsureSoundFix()
            //   ▶ 声音: AudioEventHandler.OnPlaySound → AudioManager.Instance.PlaySound(name,...,num,...)
            //           num = 事件音量 × Option.Sound.Common.Effect.Volume × Volume.Volume × 0.0001
            //           → Option 全空(0) 时每次都是 0 → 完全无声
            //   ▶ 弹孔: PlayerShot 自己 Physics.Raycast → Player.MakeHitEffectCommon
            //           → FXPlayer.MakeParticleFX → GameEvent.PlayEffect → FXManager.OnEvent_PlayFX
            //           → ObjectPoolManager.Get(fxName) → ResourceLoader.Load("Prefabs/Effects/"+fxName)
            //           poolComponent==null 时游戏自己会打 "Can't find fx : xxx"
            try
            {
                // ★★★ 必须含 Static ★★★
                //   三个探针方法 OnPlaySoundPrefix / FXPlayPostfix / MakeHitEffectPrefix 都是 static,
                //   少这个标志 → GetMethod 返回 null → new HarmonyMethod(null)
                //   → 抛 "Argument cannot be null." → 整块探针一个都装不上 (上一轮的 bug 就是这个)
                const BindingFlags LI2 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static;

                var ops = typeof(CFW.GameSystem.AudioEventHandler).GetMethod("OnPlaySound", LI2);
                if (ops != null)
                {
                    harmony.Patch(ops, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("OnPlaySoundPrefix", LI2)));
                    Debug.Log("[CFZ-Offline] 已 hook AudioEventHandler.OnPlaySound (声音事件探针)");
                }
                else Debug.LogWarning("[CFZ-Offline] AudioEventHandler.OnPlaySound 未找到");

                var fp = typeof(CFW.FXSystem.FXManager).GetMethod("_Play", LI2);
                if (fp != null)
                {
                    // ★ prefix + postfix 都装: _Play 内部若抛异常, postfix 根本不会执行,
                    //   只有 prefix 能证明"到底有没有进 _Play" (上一版只有 postfix → 0 次无法区分两种死法)
                    harmony.Patch(fp,
                        prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("FXPlayPrefix", LI2)),
                        postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("FXPlayPostfix", LI2)));
                    Debug.Log("[CFZ-Offline] 已 hook FXManager._Play (弹孔/特效探针)");
                }
                else Debug.LogWarning("[CFZ-Offline] FXManager._Play 未找到");

                // FXManager.Play —— ObjectPoolManager 把预制体加载完后的回调 (判定"预制体有没有取到")
                int fpn = 0;
                foreach (var fxpm in typeof(CFW.FXSystem.FXManager).GetMethods(LI2))
                {
                    if (fxpm.Name != "Play") continue;
                    if (fxpm.GetParameters().Length != 2) continue;
                    try
                    {
                        harmony.Patch(fxpm, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("FXManagerPlayPrefix", LI2)));
                        fpn++;
                    }
                    catch { }
                }
                Debug.Log("[CFZ-Offline] 已 hook FXManager.Play × " + fpn + " (池对象回调探针)");

                // BundleLoadJob.MakeComplete —— 每个 bundle 加载任务的最终结果 (资源到底取到没有)
                try
                {
                    var mc2 = AccessTools.Method(typeof(Common.System.BundleLoadJob), "MakeComplete");
                    if (mc2 != null)
                    {
                        harmony.Patch(mc2, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("BundleJobCompletePostfix", LI2)));
                        Debug.Log("[CFZ-Offline] 已 hook BundleLoadJob.MakeComplete (bundle 加载结果探针)");
                    }
                    else Debug.LogWarning("[CFZ-Offline] BundleLoadJob.MakeComplete 未找到");
                }
                catch (Exception eJ) { Debug.LogWarning("[CFZ-Offline] hook BundleLoadJob.MakeComplete 失败: " + eJ.Message); }

                // BundleLoadJob 构造函数 —— ★大厅角色顶替★ 的唯一切入点
                //   (资产名只在构造函数里进 job, 之后 _LoadFromCache/_LoadFromFile 全靠 job.AssetName)
                try
                {
                    int ctN = 0;
                    foreach (var ct in typeof(Common.System.BundleLoadJob).GetConstructors(LI2))
                    {
                        try
                        {
                            harmony.Patch(ct, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("BundleJobCtorPostfix", LI2)));
                            ctN++;
                        }
                        catch { }
                    }
                    Debug.Log("[CFZ-Offline] 已 hook BundleLoadJob 构造函数 × " + ctN + " (大厅角色顶替)");
                }
                catch (Exception eC) { Debug.LogWarning("[CFZ-Offline] hook BundleLoadJob 构造函数 失败: " + eC.Message); }

                int mkN = 0;
                foreach (var mk in typeof(CFW.InGame.Player.Player).GetMethods(LI2))
                {
                    if (mk.Name != "MakeHitEffectCommon") continue;
                    try
                    {
                        harmony.Patch(mk, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("MakeHitEffectPrefix", LI2)));
                        mkN++;
                    }
                    catch { }
                }
                Debug.Log("[CFZ-Offline] 已 hook Player.MakeHitEffectCommon × " + mkN + " (弹着点探针)");

                // ★AudioManager.PlaySound 是**所有**音效的最终出口 (事件链与直接调用都要经过它)
                //   → 它被调用却没声音: 问题在 AudioClip / 混音器 / Listener
                //   → 它完全不被调用:   问题在触发链 (动画事件 / SoundKey / SoundTable)
                //   这是区分"上游没触发"和"下游播不出"的唯一分水岭
                int psN = 0;
                foreach (var psm in typeof(CFW.GameSystem.Audio.AudioManager).GetMethods(LI2))
                {
                    if (psm.Name != "PlaySound") continue;
                    try
                    {
                        harmony.Patch(psm, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("AudioPlaySoundPrefix", LI2)));
                        psN++;
                    }
                    catch { }
                }
                Debug.Log("[CFZ-Offline] 已 hook AudioManager.PlaySound × " + psN + " (声音出口探针)");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 音频/特效探针 失败: " + e.Message); }

            // ---- 5) 多个"开始/准备"按钮 postfix: 触发 GameEvent.WorldTicketingComplete 进 in-game ----
            try
            {
                var postfix = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("TriggerInGamePostfix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                object[] targets = new object[] {
                    typeof(CFW.UI.Lobby.SceneForms.MatchingForm), "OnClick_BT_Ready",
                    typeof(CFW.UI.Lobby.SceneForms.CustomRoomForm), "OnClick_BT_StateChange",
                    typeof(CFW.UI.Lobby.SceneForms.FPSLobbyForm), "OnClick_BT_FastJoin",
                    typeof(CFW.UI.Lobby.SceneForms.FPSLobbyForm), "OnClick_BT_EnterRoom",
                    typeof(CFW.UI.Lobby.SceneForms.CustomRoomListForm), "OnClick_BT_EnterRoom",
                    typeof(CFW.UI.Lobby.SceneForms.CustomRoomListForm), "OnClick_BT_CreateRoom",
                };
                for (int i = 0; i < targets.Length; i += 2)
                {
                    Type tt = (Type)targets[i];
                    string nm = (string)targets[i + 1];
                    try
                    {
                        var mm = tt.GetMethod(nm, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mm != null) { harmony.Patch(mm, postfix: postfix); Debug.Log("[CFZ-Offline] 已 hook " + tt.Name + "." + nm); }
                    }
                    catch (Exception ex) { Debug.LogWarning("[CFZ-Offline] hook " + tt.Name + "." + nm + " 失败: " + ex.Message); }
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 进 in-game 失败: " + e); }

#if DEBUG
            // ---- 6) 按钮点击日志: patch uGUI Button 点击入口, 打印按钮路径 + 绑定方法名 (仅 Debug 版) ----
            try
            {
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prefix = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("ButtonPressPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                int hooked = 0;
                var oc = typeof(UnityEngine.UI.Button).GetMethod("OnPointerClick", bf);
                if (oc != null) { harmony.Patch(oc, prefix: prefix); hooked++; Debug.Log("[CFZ-Offline] 已 hook Button.OnPointerClick"); }
                var pr = typeof(UnityEngine.UI.Button).GetMethod("Press", bf, null, Type.EmptyTypes, null);
                if (pr != null) { harmony.Patch(pr, prefix: prefix); hooked++; Debug.Log("[CFZ-Offline] 已 hook Button.Press(private)"); }
                if (hooked == 0) Debug.LogWarning("[CFZ-Offline] Button 点击入口未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook Button 点击失败: " + e); }
#endif

            // ---- 7) 吞掉 LoadingUI.SetProgress 的 NRE (UISet 为 null 时会打死 _LoadUI 协程) ----
            try
            {
                var skip = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SkipPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                var sp = typeof(CFW.UI.Loading.LoadingUI).GetMethod("SetProgress",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (sp != null) { harmony.Patch(sp, prefix: skip); Debug.Log("[CFZ-Offline] 已 hook LoadingUI.SetProgress (吞 NRE)"); }
                else Debug.LogWarning("[CFZ-Offline] LoadingUI.SetProgress 未找到");
                var slp = typeof(CFW.UI.Loading.LoadingUI).GetMethod("SetLoadingProgress",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (slp != null) { harmony.Patch(slp, prefix: skip); Debug.Log("[CFZ-Offline] 已 hook LoadingUI.SetLoadingProgress (吞 NRE)"); }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook LoadingUI.SetProgress 失败: " + e); }

            // ---- 8) ★核心★ 让 BundleList 从本地 Bundle/AssetBundleList.json 读清单 (而不是服务器 URL) ----
            try
            {
                var m = typeof(Common.System.BundleList).GetMethod("LoadFromURL",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("BundleListLoadFromURLPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook BundleList.LoadFromURL -> 改走本地 AssetBundleList.json");
                }
                else Debug.LogWarning("[CFZ-Offline] BundleList.LoadFromURL 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook BundleList.LoadFromURL 失败: " + e); }

            // ---- 9) ★核心★ 让 BundleLoader._Download 直接认本地文件, 不走网络下载 ----
            try
            {
                var m = typeof(Common.System.BundleLoader).GetMethod("_Download",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("BundleDownloadPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook BundleLoader._Download -> 本地文件直通");
                }
                else Debug.LogWarning("[CFZ-Offline] BundleLoader._Download 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook BundleLoader._Download 失败: " + e); }

            // ---- 10) ★诊断★ 追踪所有 SceneManager.LoadScene / LoadSceneAsync 调用 ----
            try
            {
                var logm = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SceneLoadPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                var t = typeof(UnityEngine.SceneManagement.SceneManager);
                int cnt = 0;
                var ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "LoadScene" && ms[i].Name != "LoadSceneAsync") continue;
                    var ps = ms[i].GetParameters();
                    if (ps.Length < 1) continue;
                    var pt = ps[0].ParameterType;
                    if (pt != typeof(string) && pt != typeof(int)) continue;
                    try { harmony.Patch(ms[i], prefix: logm); cnt++; }
                    catch (Exception e2) { Debug.LogWarning("[CFZ-Offline] patch " + ms[i] + " 失败: " + e2.Message); }
                }
                Debug.Log("[CFZ-Offline] 已 hook SceneManager 场景加载 x" + cnt);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook SceneManager 失败: " + e); }

            // ---- 11) ★诊断★ AssetBundle.GetAllScenePaths ----
            try
            {
                var abType = CfzOfflineDriver.FindTypeAnywhere("AssetBundle");
                var m = abType != null ? abType.GetMethod("GetAllScenePaths",
                    BindingFlags.Public | BindingFlags.Instance) : null;
                if (m != null)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetAllScenePathsPostfix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook AssetBundle.GetAllScenePaths");
                }
                else Debug.LogWarning("[CFZ-Offline] AssetBundle.GetAllScenePaths 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook GetAllScenePaths 失败: " + e); }

            // ---- 12) ★诊断★ LoadingUI 启动痕迹 (确认 _Loading 到底有没有跑) ----
            try
            {
                var LI = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var m0 = typeof(CFW.UI.Loading.LoadingUI).GetMethod("OnEvent_LoadingStart", LI);
                if (m0 != null)
                {
                    harmony.Patch(m0, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("LoadingStartPostfix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook LoadingUI.OnEvent_LoadingStart");
                }
                var m1 = typeof(CFW.UI.Loading.LoadingUI).GetMethod("_Loading", LI);
                if (m1 != null)
                {
                    harmony.Patch(m1, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("LoadingCoroutinePrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook LoadingUI._Loading");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook LoadingUI 启动痕迹失败: " + e); }

            // ---- 13) ★诊断★ UISet.SetProgress 进度轨迹 (_Loading 走到哪一步) ----
            try
            {
                var pre = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("UISetProgressPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                CfzOfflineDriver.PatchUISetProgress(harmony, CfzOfflineDriver.FindTypeAnywhere("ILoadingUI_UISet"), pre);
                CfzOfflineDriver.PatchUISetProgress(harmony, CfzOfflineDriver.FindTypeAnywhere("LoadingUI_Solo"), pre);
                CfzOfflineDriver.PatchUISetProgress(harmony, CfzOfflineDriver.FindTypeAnywhere("LoadingUI_Team"), pre);
                CfzOfflineDriver.PatchUISetProgress(harmony, CfzOfflineDriver.FindTypeAnywhere("LoadingUI_Tutorial"), pre);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook UISet.SetProgress 失败: " + e); }

            // ---- 14) ★诊断★ BundleLoader._LoadFromFile (泛型) ----
            try
            {
                var LI2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var ms = typeof(Common.System.BundleLoader).GetMethods(LI2);
                int nLFF = 0;
                var lfp = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("LoadFromFilePrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "_LoadFromFile" || !ms[i].IsGenericMethodDefinition) continue;
                    try { harmony.Patch(ms[i], prefix: lfp); nLFF++; }
                    catch (Exception e2) { Debug.LogWarning("[CFZ-Offline] patch _LoadFromFile 失败: " + e2.Message); }
                }
                Debug.Log("[CFZ-Offline] 已 hook BundleLoader._LoadFromFile x" + nLFF);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook _LoadFromFile 失败: " + e); }

            // ---- 15) ★诊断★ 事件流追踪: GameEventManager.SendEvent + LoadingUI.OnEvent ----
            try
            {
                var pubInst = BindingFlags.Public | BindingFlags.Instance;
                var evtArgs = new Type[] { typeof(CFW.Framework.GameEvent), typeof(CFW.Framework.IGameEventParam) };
                var lfp2 = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SendEventPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                var m = typeof(CFW.Framework.GameEventManager).GetMethod("SendEvent", pubInst, null, evtArgs, null);
                if (m != null) { harmony.Patch(m, prefix: lfp2); Debug.Log("[CFZ-Offline] 已 hook GameEventManager.SendEvent"); }
                else Debug.LogWarning("[CFZ-Offline] GameEventManager.SendEvent 未找到");
                var lfp3 = new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("LoadingUIOnEventPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
                var m2 = typeof(CFW.UI.Loading.LoadingUI).GetMethod("OnEvent", pubInst, null, evtArgs, null);
                if (m2 != null) { harmony.Patch(m2, prefix: lfp3); Debug.Log("[CFZ-Offline] 已 hook LoadingUI.OnEvent"); }
                else Debug.LogWarning("[CFZ-Offline] LoadingUI.OnEvent 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 事件流失败: " + e); }

            // ---- 16) ★诊断★ AI_Tutorial_Loading.SetModeInfo (前缀+Finalizer 抓异常) ----
            try
            {
                var m = typeof(AI_Tutorial_Loading).GetMethod("SetModeInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null)
                {
                    // ★换图功能已移除★ (2026-10-09): 不再替换 SetModeInfo 体内写死的 MapIndex=1,
                    //   恒用游戏默认教学图 Transportship_ren。
                    //   原因: AI 教学模式的导航/巡逻数据写死为 Transportship_Path (InGameMode_AI_Tutorial.cs:41),
                    //   换图后 bot 按教学图坐标(y=-28)巡逻 → 掉坑/刷点异常, 得不偿失。

                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SetModeInfoPrefix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)),
                        // ★ postfix = 角色/武器改写点: 此刻 UserInfo 已经造好(角色写死 SWAT/枪写死 M4A1),
                        //   而真正的玩家要到 LoadingUI._Loading 协程里(隔着一整张地图的异步加载)才建
                        postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SetModeInfoPostfix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SetModeInfoFinalizer",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook AI_Tutorial_Loading.SetModeInfo (含 postfix=角色武器改写 + Finalizer)");
                }
                else Debug.LogWarning("[CFZ-Offline] AI_Tutorial_Loading.SetModeInfo 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook SetModeInfo 失败: " + e); }

            // ---- 17) ★诊断★ NetClient.OnEvent_Tutorial_AI_EnterComplete ----
            try
            {
                var m = typeof(CFW.Network.NetClient).GetMethod("OnEvent_Tutorial_AI_EnterComplete",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("NetClientTutorialAIPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook NetClient.OnEvent_Tutorial_AI_EnterComplete");
                }
                else Debug.LogWarning("[CFZ-Offline] NetClient.OnEvent_Tutorial_AI_EnterComplete 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook NetClient 失败: " + e); }

            // ---- 18) ★★★ 修复 1: 自动补 SceneCamera 组件 (解 66% 卡死) ★★★ ----
            try
            {
                var p = typeof(CFW.InGame.GameCamera.CameraUtility).GetProperty("SceneCamera",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var gm = p != null ? p.GetGetMethod(true) : null;
                if (gm != null)
                {
                    harmony.Patch(gm, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("SceneCameraGetterPostfix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook CameraUtility.get_SceneCamera (自动补组件)");
                }
                else Debug.LogWarning("[CFZ-Offline] CameraUtility.get_SceneCamera 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook get_SceneCamera 失败: " + e); }

            // ---- 19) ★★★ 修复 2: 强制教程路径 Loading_End (解开加载界面) ★★★ ----
            try
            {
                var m = typeof(CFW.Network.NetClient).GetMethod("OnEvent_LoadingComplete",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("NetClientLoadingCompletePrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook NetClient.OnEvent_LoadingComplete (强制 Loading_End)");
                }
                else Debug.LogWarning("[CFZ-Offline] NetClient.OnEvent_LoadingComplete 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook OnEvent_LoadingComplete 失败: " + e); }

            // ---- 20) ★诊断★ 玩家生成链路 (定位 66% 卡点) ----
            try
            {
                const BindingFlags LI = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var pm = typeof(CFW.InGame.Player.PlayerManager);
                int np = 0;
                var m1 = pm.GetMethod("CreatePlayer", LI);
                if (m1 != null)
                {
                    harmony.Patch(m1, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("CreatePlayerPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    np++;
                }
                var m2 = pm.GetMethod("_CreatePlayer", LI);
                if (m2 != null)
                {
                    harmony.Patch(m2, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("CreatePlayerCoroutinePrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    np++;
                }
                var ms = pm.GetMethods(LI);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "AddPlayer") continue;
                    try
                    {
                        harmony.Patch(ms[i], prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("PlayerManagerAddPlayerPrefix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                        np++;
                    }
                    catch { }
                }
                var pms = typeof(CFW.InGame.Player.Player).GetMethods(LI);
                for (int i = 0; i < pms.Length; i++)
                {
                    if (pms[i].Name != "OnCreate") continue;
                    try
                    {
                        harmony.Patch(pms[i], prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("PlayerOnCreatePrefix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                        np++;
                    }
                    catch { }
                }
                Debug.Log("[CFZ-Offline] 已 hook 玩家生成链路 x" + np);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 玩家生成链路 失败: " + e); }

            // ---- 21) ★诊断★ QV/PV 模型路径探针 + AI 回合开始护盾 ----
            try
            {
                const BindingFlags LI2 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var gm = typeof(Common.System.ResourceLoader).GetMethod("GetResourcePathCheckRegionFolder", LI2);
                if (gm != null)
                {
                    harmony.Patch(gm,
                        prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetResPathPrefix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetResPathPostfix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook ResourceLoader.GetResourcePathCheckRegionFolder (路径探针)");
                }
                else Debug.LogWarning("[CFZ-Offline] GetResourcePathCheckRegionFolder 未找到");

                var ors = typeof(InGameMode_AI_Tutorial).GetMethod("OnRoundStarting", LI2);
                if (ors != null)
                {
                    harmony.Patch(ors,
                        prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("AIRoundStartingPrefix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("AIRoundStartingPostfix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook InGameMode_AI_Tutorial.OnRoundStarting (QV 未就绪护盾 + 解锁武器袋)");
                }
                else Debug.LogWarning("[CFZ-Offline] InGameMode_AI_Tutorial.OnRoundStarting 未找到");

                // ★武器袋随时可开★ OnShow_SelectPopup 的门槛是 IsEnable()(=袋按钮亮着),
                //   而按钮会被 ①离复活点>20m ②开火/受击/死亡/丢枪 关掉。
                //   在"按 B 弹窗"之前强制 SetEnable(true) → 两道限制都没了, 随时能打开。
                var wbs = typeof(CFW.UI.InGameUI.InGameUI_SelectWeaponBag).GetMethod("OnShow_SelectPopup", LI2);
                if (wbs != null)
                {
                    harmony.Patch(wbs, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("WeaponBagShowPrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook InGameUI_SelectWeaponBag.OnShow_SelectPopup (B 键随时呼出背包)");
                }
                else Debug.LogWarning("[CFZ-Offline] OnShow_SelectPopup 未找到");

                // ★模式选择界面: BT_AX_CommingSoon 改成 [AI Combat] 入口★
                //   GameSelectForm.StartForm 每次打开模式选择都跑 → 接管按钮点击(等效 F9) + 改 Tx_CommingSoon 文本
                var gsf = typeof(CFW.UI.Lobby.SceneForms.GameSelectForm).GetMethod("StartForm",
                    BindingFlags.Public | BindingFlags.Instance);
                if (gsf != null)
                {
                    harmony.Patch(gsf, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GameSelectFormStartPostfix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook GameSelectForm.StartForm (AI Combat 按钮)");
                }
                else Debug.LogWarning("[CFZ-Offline] GameSelectForm.StartForm 未找到");

                // ★开镜输入探针 (SetZoom/_UpdateMouse/OnMouseMove) 已随根因定位完成而移除★ (2026-10-09)
                //   根因: Option.Mouse.FPS.Scope.Value 离线恒 0 → 开镜分支增量 ×0。修复在 AIRoundStartingPostfix。
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 模型路径/回合护盾 失败: " + e); }

            // ---- 22) ★修复★ 强制角色/武器走 AssetBundle (Resources 里没有对战角色预制体) ----
            try
            {
                const BindingFlags LI3 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var np = typeof(Common.System.ResourceLoader).GetMethod("_NeedPrefabResource", LI3);
                if (np != null)
                {
                    harmony.Patch(np, prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("NeedPrefabResourcePrefix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook ResourceLoader._NeedPrefabResource (强制走 bundle)");
                }
                else Debug.LogWarning("[CFZ-Offline] _NeedPrefabResource 未找到");

                try
                {
                    var ib = typeof(Common.System.BundleList).GetMethod("IsBundle", LI3, null,
                        new Type[] { typeof(string), typeof(string) }, null);
                    if (ib != null)
                    {
                        harmony.Patch(ib,
                            prefix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("IsBundle2Prefix",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)),
                            postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("IsBundle2Postfix",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                        Debug.Log("[CFZ-Offline] 已 hook BundleList.IsBundle(string,string) (探针)");
                    }
                    else Debug.LogWarning("[CFZ-Offline] BundleList.IsBundle(string,string) 未找到");
                }
                catch (Exception e2) { Debug.LogWarning("[CFZ-Offline] hook IsBundle 探针失败: " + e2.Message); }

                // ---- 22b) ★bundle 名解析★ hook BundleList.GetBundleInfo ----
                // chart 的键 = ResourceName (被 ResourcePath[0] 剥前缀覆盖) = "character/fps_m_swat"
                // 而游戏传 'FPS_M_SWAT' → TryGetValue("fps_m_swat") 必然 miss
                // → bundleInfo.Name 为空 → 游戏认为"没有这个 bundle" → 退回 Resources → 模型永远建不起来
                // 修复: postfix 里按 Name 字段 / 键后缀模糊解析真实条目
                try
                {
                    var gb = typeof(Common.System.BundleList).GetMethod("GetBundleInfo", LI3, null,
                        new Type[] { typeof(string) }, null);
                    if (gb != null)
                    {
                        harmony.Patch(gb, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetBundleInfoPostfix",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                        Debug.Log("[CFZ-Offline] 已 hook BundleList.GetBundleInfo (bundle 名模糊解析)");
                    }
                    else Debug.LogWarning("[CFZ-Offline] BundleList.GetBundleInfo 未找到");
                }
                catch (Exception e3) { Debug.LogWarning("[CFZ-Offline] hook GetBundleInfo 失败: " + e3.Message); }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook _NeedPrefabResource 失败: " + e); }

            // ---- 23) ★按键表★ hook CFW.GameSystem.InputManager.Update (每帧: 探测/修复按键表 + WASD 旁路) ----
            try
            {
                var imType = typeof(CFW.GameSystem.InputManager);
                var um = imType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (um != null)
                {
                    harmony.Patch(um, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("InputManagerUpdatePostfix",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
                    Debug.Log("[CFZ-Offline] 已 hook CFW.GameSystem.InputManager.Update (按键表修复 + WASD 旁路)");
                }
                else Debug.LogWarning("[CFZ-Offline] CFW.GameSystem.InputManager.Update 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] hook InputManager.Update 失败: " + e); }

            // ---- 24) ★移动兜底★ 已停用 (2026-10-09) ----
            // 日志证实 Shot != null (从未触发"自调 UpdateMyPlayer"), 游戏自己的移动链路是通的, 此 hook 闲置。
            // 若之后出现"完全走不动", 把下面注释还原即可。
            // try
            // {
            //     var pm = typeof(CFW.InGame.Player.Player).GetMethod("PlayerMoveFixedUpdate",
            //         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            //     if (pm != null)
            //     {
            //         harmony.Patch(pm, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("PlayerMoveFixedUpdatePostfix",
            //             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
            //         Debug.Log("[CFZ-Offline] 已 hook Player.PlayerMoveFixedUpdate (移动兜底)");
            //     }
            //     else Debug.LogWarning("[CFZ-Offline] Player.PlayerMoveFixedUpdate 未找到");
            // }
            // catch (Exception e) { Debug.LogError("[CFZ-Offline] hook PlayerMoveFixedUpdate 失败: " + e); }

            // ---- 25) ★遥测兜底★ 跳过 NetClient.OnSend_TLog_* ----
            // NetClient.cs 第2517-2523行 OnSend_TLog_WeaponFOV ★ 没有 null 守卫:
            //     clientToWorldProxy.WEAPON_FOV_NOTIFY(...)     ← 离线 → clientToWorldProxy == null → NRE
            // 而这个 NRE 会从 SceneCamera_FOV.OnEvent_ChangeZoomStepImmediate 一路冲穿:
            //     InputManager._UpdateMouse (第449-462行)
            //       → PlayerInputEvent.OnEvent_UpdateMouse
            //         → Input_TryChangeWeaponSlotLower → SendWeaponChange → PlayerShot.OnWeaponChange
            //           → Player.OnChangeZoomStep → SceneCamera_FOV → TLog → ★ NRE
            //     → 异常冲出 _UpdateMouse → ★ 视角旋转代码再也没有机会执行 ★ (也无法换武器)
            // 离线时遥测毫无意义, 直接把所有 void 型 OnSend_TLog_* 跳过。
            try
            {
                var fin = typeof(Patcher).GetMethod("SwallowAnyException",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fin == null) Debug.LogError("[CFZ-Offline] SwallowAnyException 反射返回 null → TLog 兜底未安装!");
                else
                {
                    // (a) ★直接对准 TLog 类★ 栈里是 CFW.Network.Tencent.TLog.SendLog_WeaponState
                    //     Finalizer 语义: 返回 null = 不抛出 → NRE 在这里被截住
                    Type tlogType = null;
                    try { tlogType = typeof(CFW.Network.NetClient).Assembly.GetType("CFW.Network.Tencent.TLog"); } catch { }
                    if (tlogType == null) { try { tlogType = Type.GetType("CFW.Network.Tencent.TLog, Assembly-CSharp"); } catch { } }
                    int tn = 0;
                    if (tlogType != null)
                    {
                        foreach (var m in tlogType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters) continue;
                            try { harmony.Patch(m, finalizer: new HarmonyMethod(fin)); tn++; } catch { }
                        }
                        Debug.Log("[CFZ-Offline] 已给 CFW.Network.Tencent.TLog 的 " + tn + " 个方法加异常吞噬 (离线遥测)");
                    }
                    else Debug.LogWarning("[CFZ-Offline] ★未找到 CFW.Network.Tencent.TLog★");

                    // (b) NetClient 里所有方法名含 "TLog" 的 (含 OnSend_TLog_WeaponFOV)
                    int n2 = 0;
                    var names = new System.Text.StringBuilder();
                    foreach (var m in typeof(CFW.Network.NetClient).GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m.Name.IndexOf("TLog", StringComparison.Ordinal) < 0) continue;
                        names.Append(m.Name).Append(' ');
                        try { harmony.Patch(m, finalizer: new HarmonyMethod(fin)); n2++; }
                        catch (Exception em) { Debug.LogWarning("[CFZ-Offline] TLog patch " + m.Name + " 失败: " + em.Message); }
                    }
                    Debug.Log("[CFZ-Offline] NetClient 含TLog 的方法 " + n2 + " 个: " + (names.Length > 0 ? names.ToString() : "★一个都没匹配到★"));
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] hook TLog 失败: " + e.Message); }

            // ---- 26) ★刷点兜底★ 已停用 (2026-10-09) ----
            // 原逻辑: GetRespawnPoint 查不到时返回一个真实存在的刷点 (防 NRE 卡加载)。
            // 默认教学图的刷点数据是全的, 此兜底从未真正触发过 → 停用。
            // 若进图出现"卡加载/不能动", 把下面注释还原。
            // try
            // {
            //     const BindingFlags LI4 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            //     int hooked = 0;
            //     foreach (var m in typeof(CFW.GameSystem.InGameData_RespawnPoint).GetMethods(LI4))
            //     {
            //         if (m.Name != "GetRespawnPoint") continue;
            //         var ps = m.GetParameters();
            //         if (ps.Length == 1 && ps[0].ParameterType == typeof(ETeamID))
            //         {
            //             harmony.Patch(m, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetRespawnPoint1Postfix",
            //                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
            //             hooked++;
            //         }
            //         else if (ps.Length == 2 && ps[0].ParameterType == typeof(ETeamID) && ps[1].ParameterType == typeof(int))
            //         {
            //             harmony.Patch(m, postfix: new HarmonyMethod(typeof(CfzOfflineDriver).GetMethod("GetRespawnPointPostfix",
            //                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)));
            //             hooked++;
            //         }
            //     }
            //     Debug.Log("[CFZ-Offline] 已 hook InGameData_RespawnPoint.GetRespawnPoint x" + hooked + " (刷点兜底)");
            // }
            // catch (Exception e) { Debug.LogError("[CFZ-Offline] hook 刷点兜底 失败: " + e); }

            // ---- 27) ★敌人难度★ 把教学 AI 的硬编码"技能常量"改成运行时读 Loadout (Transpiler) ----
            //   这些值都是**方法体内的常量**, 不是参数/字段 → 只能改 IL。
            //   注入的是 call 指令, 每次执行都重新取值 → ini 改完按 F10 立刻生效。
            //   日志里的"换掉 N 处"必须是预期数量, 是 0 就说明游戏这版 IL 对不上 → 该项难度不生效(但不会崩)。
            try
            {
                const BindingFlags LI5 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                int okTp = 0;

                MethodBase mAim = typeof(CFW.InGame.Player.AI_TutorialPlayer).GetMethod("SearchTarget", LI5);
                if (mAim != null)
                {
                    _tpHits = 0; harmony.Patch(mAim, transpiler: TpMethod("AimTranspiler")); okTp++;
                    Debug.Log("[CFZ-Offline][难度] AI_TutorialPlayer.SearchTarget ✓ 换掉 " + _tpHits + " 处常量 (目标 4)");
                }
                else Debug.LogWarning("[CFZ-Offline][难度] AI_TutorialPlayer.SearchTarget 未找到");

                MethodBase mFire = typeof(CFW.InGame.Player.AI_TutorialPlayerState_FireStarting).GetMethod("Update", LI5);
                if (mFire != null)
                {
                    _tpHits = 0; harmony.Patch(mFire, transpiler: TpMethod("FireTranspiler")); okTp++;
                    Debug.Log("[CFZ-Offline][难度] FireStarting.Update ✓ 换掉 " + _tpHits + " 处常量 (目标 5: 抖动4+间隔1)");
                }
                else Debug.LogWarning("[CFZ-Offline][难度] FireStarting.Update 未找到");

                MethodBase mHit = typeof(AI_TutorialHitCheck).GetMethod("CaculateHit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (mHit != null)
                {
                    _tpHits = 0; harmony.Patch(mHit, transpiler: TpMethod("HitCheckTranspiler")); okTp++;
                    Debug.Log("[CFZ-Offline][难度] AI_TutorialHitCheck.CaculateHit ✓ 换掉 " + _tpHits + " 处常量 (目标 3: 半径1+距离2)");
                }
                else Debug.LogWarning("[CFZ-Offline][难度] AI_TutorialHitCheck.CaculateHit 未找到");

                MethodBase mDie = typeof(CFW.InGame.Player.AI_TutorialPlayerState_die).GetMethod("Update", LI5);
                if (mDie != null)
                {
                    _tpHits = 0; harmony.Patch(mDie, transpiler: TpMethod("DieTranspiler")); okTp++;
                    Debug.Log("[CFZ-Offline][难度] AI_TutorialPlayerState_die.Update ✓ 换掉 " + _tpHits + " 处常量 (目标 3: 复活1+血量2)");
                }
                else Debug.LogWarning("[CFZ-Offline][难度] AI_TutorialPlayerState_die.Update 未找到");

                MethodBase mSpd = typeof(NpcPathFinder).GetMethod("Start", LI5);
                if (mSpd != null)
                {
                    _tpHits = 0; harmony.Patch(mSpd, transpiler: TpMethod("SpeedTranspiler")); okTp++;
                    Debug.Log("[CFZ-Offline][难度] NpcPathFinder.Start ✓ 换掉 " + _tpHits + " 处常量 (目标 1: 速度)");
                }
                else Debug.LogWarning("[CFZ-Offline][难度] NpcPathFinder.Start 未找到");

                Debug.Log("[CFZ-Offline][难度] Transpiler 注册完成: " + okTp + "/5 个方法");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 注册难度 Transpiler 失败: " + e); }
        }

        // ==================== ★敌人难度★ (Harmony Transpiler) ======================================
        //
        //  教学 AI 没有任何"难度"配置 —— 它的"技能"全是硬编码常量 (没有字段/枚举/数据表可改)。
        //  这里用 Transpiler 把这些常量换成 "运行时调 Loadout 的 getter", 于是 ini 改完按 F10
        //  立刻生效 (注入的是 call 指令, 不是编译期常量)。计数 0 = 该方法的 IL 对不上 → 只有那一项不生效。
        //
        //  【每个常量的原始出处】(原值见 Loadout.DifficultyPreset 的"普通"档)
        //    AI_TutorialPlayer.cs:187-188                 Random.Range(-7f,7f) / Random.Range(-2f,2f)  ← 瞄准抖动 = 命中率
        //    AI_TutorialPlayerState_FireStarting.cs:65-66 同上 (开火时再抖一次)
        //    AI_TutorialPlayerState_FireStarting.cs:60    FireTime >= 0.2f                          ← 开火间隔
        //    AI_TutorialHitCheck.cs:18                    IntersectSphereCastAll(ray, 6f, 80f, 1)     ← 视野(半径 6 / 距离 80)
        //    AI_TutorialHitCheck.cs:37                    IntersectRayAll(pos, dir, 80f, ...)         ← 视线距离
        //    AI_TutorialPlayerState_die.cs:51             ResPawnTime >= 3f                           ← 复活延迟
        //    AI_TutorialPlayerState_die.cs:57-58          MaxHP = 100 / HP = 100                      ← 复活血量(写死, 与角色血量无关)
        //    NpcPathFinder.cs:19                          base.speed = 8f                             ← 移动速度
        //
        //  ★匹配规则: 只按"常量值"匹配 (1e-6 容差), 命中即换成 call。同方法里其它常量
        //    (0.7f / 57.29578f / 1.4f / 0f) 与这些值不同, 不会误伤。
        static int _tpHits = 0;

        static HarmonyMethod TpMethod(string name)
        {
            return new HarmonyMethod(typeof(Patcher).GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        static bool SameF(float a, float b) { return Math.Abs(a - b) <= 1e-6f; }

        // 把 IL 里的 float 常量 from[i] 换成 call Loadout.getter[i]()
        //   ★原地改 opcode/operand, 不新建指令 —— 这样原指令上的 labels / 异常块引用都保留★
        static IEnumerable<CodeInstruction> MapFloat(IEnumerable<CodeInstruction> ins, float[] from, string[] getter)
        {
            var outp = new List<CodeInstruction>();
            foreach (CodeInstruction c in ins)
            {
                int hit = -1;
                if (c.opcode == OpCodes.Ldc_R4 && c.operand is float)
                {
                    float v = (float)c.operand;
                    for (int i = 0; i < from.Length; i++) if (SameF(v, from[i])) { hit = i; break; }
                }
                if (hit < 0) { outp.Add(c); continue; }
                MethodInfo mi = typeof(Loadout).GetMethod(getter[hit], BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { outp.Add(c); continue; }
                c.opcode = OpCodes.Call;                        // 栈上是同一个 float, 位置不用动
                c.operand = mi;
                outp.Add(c);
                _tpHits++;
            }
            return outp;
        }

        // 把 IL 里的 int 常量 from[i] 换成 call Loadout.getter[i]()
        static IEnumerable<CodeInstruction> MapInt(IEnumerable<CodeInstruction> ins, int[] from, string[] getter)
        {
            var outp = new List<CodeInstruction>();
            foreach (CodeInstruction c in ins)
            {
                int lit = int.MinValue; bool isInt = false;
                if (c.opcode == OpCodes.Ldc_I4) { lit = Convert.ToInt32(c.operand); isInt = true; }
                else if (c.opcode == OpCodes.Ldc_I4_S) { lit = Convert.ToInt32(c.operand); isInt = true; }
                int hit = -1;
                if (isInt) for (int i = 0; i < from.Length; i++) if (lit == from[i]) { hit = i; break; }
                if (hit < 0) { outp.Add(c); continue; }
                MethodInfo mi = typeof(Loadout).GetMethod(getter[hit], BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { outp.Add(c); continue; }
                c.opcode = OpCodes.Call;                        // ldc.i4(.s) → call (栈上都是 int)
                c.operand = mi;
                outp.Add(c);
                _tpHits++;
            }
            return outp;
        }

        // ① 索敌/开火时的瞄准抖动 (AI_TutorialPlayer.SearchTarget)
        public static IEnumerable<CodeInstruction> AimTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MapFloat(ins, new float[] { -7f, 7f, -2f, 2f },
                                new string[] { "AimMinX", "AimMaxX", "AimMinY", "AimMaxY" });
        }

        // ② 开火间隔 0.2s + 开火时再抖一次 (AI_TutorialPlayerState_FireStarting.Update)
        public static IEnumerable<CodeInstruction> FireTranspiler(IEnumerable<CodeInstruction> ins)
        {
            IEnumerable<CodeInstruction> step1 =
                MapFloat(ins, new float[] { -7f, 7f, -2f, 2f },
                             new string[] { "AimMinX", "AimMaxX", "AimMinY", "AimMaxY" });
            return MapFloat(step1, new float[] { 0.2f }, new string[] { "GetFireInterval" });
        }

        // ③ 视野: 球扫半径 6 + 球扫距离/视线距离 80 (AI_TutorialHitCheck.CaculateHit)
        public static IEnumerable<CodeInstruction> HitCheckTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MapFloat(ins, new float[] { 6f, 80f }, new string[] { "GetSightRadius", "GetSightRange" });
        }

        // ④ 复活延迟 3s + 复活血量 100 (AI_TutorialPlayerState_die.Update)
        public static IEnumerable<CodeInstruction> DieTranspiler(IEnumerable<CodeInstruction> ins)
        {
            IEnumerable<CodeInstruction> step1 = MapFloat(ins, new float[] { 3f }, new string[] { "GetRespawnDelay" });
            return MapInt(step1, new int[] { 100 }, new string[] { "GetBotHp" });
        }

        // ⑤ 移动速度 8 (NpcPathFinder.Start)
        public static IEnumerable<CodeInstruction> SpeedTranspiler(IEnumerable<CodeInstruction> ins)
        {
            return MapFloat(ins, new float[] { 8f }, new string[] { "GetMoveSpeed" });
        }

        // ⑥ [地图] 把 SetModeInfo 体内写死的 MapIndex = 1 换成运行时读配置
        //   ★为什么必须改 IL★ SetModeInfo:137 派发 Loading_Start 时, LoadingUI._Loading 协程会**同步**
        //     一直跑到 :141 的 GetSceneName(param.MapIndex) —— 那时 IsLoadComplete 已经是 true, :135 的
        //     while 不 yield, 于是整段在方法体内部就跑完了 → postfix 再写 MapIndex 根本来不及。
        //   IL 形态 (离线核对过, 只有这一处): ldsfld Param_LoadingStart / ldc.i4.1 / stfld MapIndex
        //   (RoundCount = 1 也是 ldc.i4.1, 但 stfld 的字段名不同, 不会误伤)
        public static IEnumerable<CodeInstruction> SetModeInfoMapTranspiler(IEnumerable<CodeInstruction> ins)
        {
            var list = new List<CodeInstruction>(ins);
            MethodInfo getter = typeof(Loadout).GetMethod("GetMapId", BindingFlags.Public | BindingFlags.Static);
            if (getter == null) return list;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Stfld) continue;
                var f = list[i].operand as FieldInfo;
                if (f == null || f.Name != "MapIndex") continue;
                CodeInstruction prev = list[i - 1];
                if (prev.opcode != OpCodes.Ldc_I4_1 && prev.opcode != OpCodes.Ldc_I4 &&
                    prev.opcode != OpCodes.Ldc_I4_S) continue;
                prev.opcode = OpCodes.Call;      // 原地改 opcode → 原指令上的标签/异常块都不丢
                prev.operand = getter;
                _tpHits++;
            }
            return list;
        }

        // 吞掉任意异常 (Harmony Finalizer 语义: 返回 null 表示"不抛出")
        public static Exception SwallowAnyException(Exception __exception) { return null; }

        public static void ApplyResolutionPrefix()
        {
            try
            {
                int sw = UnityEngine.Display.main.systemWidth;
                Common.System.Option.ResolutionType target = Common.System.Option.ResolutionType.Resolution_1280x720;
                if (sw >= 3840) target = Common.System.Option.ResolutionType.Resolution_3840x2160;
                else if (sw >= 2560) target = Common.System.Option.ResolutionType.Resolution_2560x1440;
                else if (sw >= 1920) target = Common.System.Option.ResolutionType.Resolution_1920x1080;
                else if (sw >= 1600) target = Common.System.Option.ResolutionType.Resolution_1920x1080;
                else if (sw >= 1366) target = Common.System.Option.ResolutionType.Resolution_1366x768;
                else if (sw >= 1280) target = Common.System.Option.ResolutionType.Resolution_1280x720;
                else target = Common.System.Option.ResolutionType.Resolution_1024x768;

                int sh = UnityEngine.Display.main.systemHeight;
                // ★ Graphic.cs:8-10  两个独立字段:
                //     Resolution          ← 大厅/窗口用 (Option.ApplyResolution)
                //     Resolution_InGame   ← ★进游戏后用 (Option.ApplyResolution_InGame)
                //   只改前者 = 一进图被后者(800x600)覆盖 → "进入游戏时分辨率变回 800x600"
                var win = Common.System.Option.Option.Graphic.Resolution;
                var ing = Common.System.Option.Option.Graphic.Resolution_InGame;
                var curWin = win.CurrentResolution.Value;
                var curIng = ing.CurrentResolution.Value;
                bool ch = false;
                if (curWin != target) { win.CurrentResolution.Value = target; ch = true; }
                if (!win.IsFullScreen.Value) { win.IsFullScreen.Value = true; ch = true; }
                if (curIng != target) { ing.CurrentResolution.Value = target; ch = true; }
                if (!ing.IsFullScreen.Value) { ing.IsFullScreen.Value = true; ch = true; }
                if (ch)
                {
                    Debug.Log("[CFZ-Offline] 分辨率已修正: 大厅 " + curWin + " -> " + target +
                              " | 进图 " + curIng + " -> " + target + " (屏宽=" + sw + ")");
                    try { Common.System.Option.Option.Save(); } catch (Exception ex) { Debug.LogWarning("[CFZ-Offline] Option.Save 失败: " + ex.Message); }
                }
                // 兜底: 若此刻实际分辨率仍不等于屏幕原生分辨率, 直接设回来
                if (UnityEngine.Screen.width != sw || UnityEngine.Screen.height != sh)
                {
                    int ow = UnityEngine.Screen.width, oh = UnityEngine.Screen.height;
                    UnityEngine.Screen.SetResolution(sw, sh, UnityEngine.FullScreenMode.FullScreenWindow);
                    Debug.Log("[CFZ-Offline] 分辨率兜底: Screen " + ow + "x" + oh + " -> " + sw + "x" + sh);
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ApplyResolutionPrefix 异常: " + e); }
        }
    }

    // ==================== ★INI 键位表★ ====================
    //
    //  只干一件事: 让"键位表"可配置。
    //
    //  背景: 游戏自己的 Option.Key.FPS 整张是空的
    //        (实测 map.Count=0, GetKey 全 None, 连 GetDefaultKey 也是 None → SetDefault 也救不回来)
    //        InputTable.UpdateTable() 的唯一派发口 InputKey.CheckInput():
    //            if (GameKey != GameKey.None) Key = Option.Key.FPS.GetKey(GameKey);   ← 全 None
    //            if (Key != KeyCode.None) { ...读 Input.GetKey... }                    ← 整段跳过
    //            IsRaiseEvent = (currState == KeyState);                               ← 永远 false
    //        → 开火/跳跃/换武器/换弹/蹲/走 一个事件都发不出来。
    //        所以只能由本 mod 的旁路自己读物理键, 键位就放在这个 ini 里。
    //
    //  文件: <游戏根目录>\Mods\CFZOfflineMod\Config.ini   (与 CFZOfflineMod.dll 同目录)
    //        取不到 DLL 路径时退回 <游戏根目录>\Mods\Config.ini
    //  ★不存在会自动生成一份带注释的默认配置 (CF 标准键位)
    //  ★改完存盘后, 进游戏按 F10 立即重新加载, 不用重启
    //  ★某个动作留空 = 禁用该动作
    //
    //  滚轮换武器**不在这里配** —— 引擎原生处理, 与本表无关:
    //      InputManager.cs:459 直接读 Mouse ScrollWheel → Input_UpdateMouse
    //      → PlayerInputEvent.cs:122-134 派发 TryChangeWeaponSlotUpper/Lower

    public static class ModConfig
    {
        public const string FileName = "Config.ini";
        public const string Sec = "按键";
        public const string SecLoadout = "角色武器";        // ★ 角色/武器段 (见 Loadout 类)
        public const string SecEnemy = "敌人";              // ★ 敌人(AI)段 (见 Loadout 类)
        public const string SecMap = "地图";                // ★ 地图段 (见 Loadout 类)

        static readonly System.Collections.Generic.Dictionary<string, string> kv =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly System.Collections.Generic.Dictionary<string, KeyCode[]> kcache =
            new System.Collections.Generic.Dictionary<string, KeyCode[]>(StringComparer.OrdinalIgnoreCase);

        public static bool Loaded = false;
        public static string Path = "";
        public static string Status = "未加载";

        static string curSec = "";

        // ---------------- 路径 ----------------
        public static string ResolvePath()
        {
            try
            {
                string loc = null;
                try { loc = Assembly.GetExecutingAssembly().Location; } catch { }
                string dir = string.IsNullOrEmpty(loc) ? null : System.IO.Path.GetDirectoryName(loc);
                if (string.IsNullOrEmpty(dir))
                {
                    var up = System.IO.Directory.GetParent(Application.dataPath);
                    dir = System.IO.Path.Combine(up != null ? up.FullName : Application.dataPath, "Mods");
                }
                return System.IO.Path.Combine(dir, FileName);
            }
            catch { return FileName; }
        }

        // ---------------- 加载 ----------------
        public static void EnsureAndLoad()
        {
            kv.Clear(); kcache.Clear(); curSec = ""; Loaded = false;
            Path = ResolvePath();
            try
            {
                if (!System.IO.File.Exists(Path))
                {
                    var d = System.IO.Path.GetDirectoryName(Path);
                    if (!string.IsNullOrEmpty(d) && !System.IO.Directory.Exists(d)) System.IO.Directory.CreateDirectory(d);
                    System.IO.File.WriteAllText(Path, DefaultIni, new System.Text.UTF8Encoding(true));
                    Debug.LogWarning("[CFZ-Offline][INI] 已生成默认键位配置: " + Path);
                }
                else EnsureSections();          // ★ 老配置自动补上新增的段 (只追加缺失段, 已有值一律不动)

                foreach (var raw in System.IO.File.ReadAllLines(Path, System.Text.Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0) continue;
                    char c0 = s[0];
                    if (c0 == ';' || c0 == '#' || c0 == '/') continue;              // 整行注释
                    if (c0 == '[')
                    {
                        int e = s.IndexOf(']');
                        curSec = e > 1 ? s.Substring(1, e - 1).Trim() : "";
                        continue;
                    }
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1);
                    int sc = v.IndexOf(';');                                        // 行尾注释
                    if (sc >= 0) v = v.Substring(0, sc);
                    v = v.Trim();
                    if (k.Length == 0) continue;
                    kv[curSec + "|" + k] = v;
                    if (!kv.ContainsKey("|" + k)) kv["|" + k] = v;                   // 不写段名也能取到
                }
                Loaded = true;
                Status = "已加载 " + kv.Count + " 项";
                Debug.Log("[CFZ-Offline][INI] 键位表 " + Status + " | " + Path);
            }
            catch (Exception e)
            {
                Status = "读取失败: " + e.Message;
                Debug.LogError("[CFZ-Offline][INI] " + Status + " | 路径=" + Path);
            }
        }

        // ---------------- 取值 ----------------
        static string S(string sec, string def, params string[] names)
        {
            string v;
            foreach (var n in names)
            {
                if (kv.TryGetValue(sec + "|" + n, out v)) return v;
                if (kv.TryGetValue("|" + n, out v)) return v;
            }
            return def;
        }

        // ★ 只按段名取值, **不回退**到"无段名"的扁平表。
        //   为什么: [按键] 段里也有"主武器/副武器/近战/投掷"这些键, 若允许回退, 读角色武器段时会
        //   拿到 Alpha1 这种键位值。所以角色/武器一律用这个入口。
        public static string SV(string sec, string def, params string[] names)
        {
            string v;
            foreach (var n in names)
            {
                if (kv.TryGetValue(sec + "|" + n, out v)) return v == null ? def : v.Trim();
            }
            return def;
        }

        // 这个段在 ini 里是否存在 (用来区分"用户没配"与"用户配成空")
        public static bool HasSection(string sec)
        {
            string pre = sec + "|";
            foreach (var k in kv.Keys) if (k.StartsWith(pre, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---------------- 缺段自动补齐 (老用户升级后新段不会自己出现) ----------------
        static void EnsureSections()
        {
            try
            {
                string text = System.IO.File.ReadAllText(Path, System.Text.Encoding.UTF8);
                var blocks = SplitSections(DefaultIni);
                int n = 0;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (text.IndexOf("[" + blocks[i].Key + "]", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    System.IO.File.AppendAllText(Path, "\r\n" + blocks[i].Value + "\r\n", new System.Text.UTF8Encoding(true));
                    Debug.LogWarning("[CFZ-Offline][INI] 已自动补充缺失的段 [" + blocks[i].Key + "] → " + Path);
                    n++;
                }
                if (n > 0) Debug.LogWarning("[CFZ-Offline][INI] 共补 " + n + " 段 (原有配置未改动, 只是追加)");
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][INI] 补段失败(不影响启动): " + e.Message); }
        }

        static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> SplitSections(string ini)
        {
            var res = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(ini)) return res;
            string[] lines = ini.Replace("\r\n", "\n").Split('\n');
            string cur = null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                if (s.StartsWith("["))
                {
                    if (cur != null) res.Add(new System.Collections.Generic.KeyValuePair<string, string>(cur, sb.ToString().TrimEnd()));
                    int e = s.IndexOf(']');
                    cur = (e > 1) ? s.Substring(1, e - 1).Trim() : null;
                    sb.Length = 0;
                    if (cur == null) continue;
                    sb.AppendLine(s);
                    continue;
                }
                if (cur != null) sb.AppendLine(s);
            }
            if (cur != null) res.Add(new System.Collections.Generic.KeyValuePair<string, string>(cur, sb.ToString().TrimEnd()));
            return res;
        }

        // 取一组物理键 (带缓存; 键名写错 → 忽略该键并打印警告)
        public static KeyCode[] K(params string[] names)
        {
            string id = string.Join("|", names);
            KeyCode[] r;
            if (kcache.TryGetValue(id, out r)) return r;
            r = ParseKeys(S(Sec, "", names));
            kcache[id] = r;
            return r;
        }

        static KeyCode[] ParseKeys(string raw)
        {
            var list = new System.Collections.Generic.List<KeyCode>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var part in raw.Split(',', ';', ' ', '\t', '/'))
                {
                    string s = part.Trim();
                    if (s.Length == 0) continue;
                    KeyCode kc;
                    if (TryKey(s, out kc)) list.Add(kc);
                    else Debug.LogWarning("[CFZ-Offline][INI] 无法识别的键名: '" + s + "' (已忽略, 请用 Unity KeyCode 名)");
                }
            }
            return list.ToArray();
        }

        static bool TryKey(string s, out KeyCode kc)
        {
            kc = KeyCode.None;
            int num;
            if (int.TryParse(s, out num)) { kc = (KeyCode)num; return kc != KeyCode.None; }
            string t = s;
            if (t.StartsWith("KeyCode.", StringComparison.OrdinalIgnoreCase)) t = t.Substring(8);
            switch (t.ToLowerInvariant())
            {
                case "鼠标左键": case "左键": case "鼠标左": t = "Mouse0"; break;
                case "鼠标右键": case "右键": t = "Mouse1"; break;
                case "鼠标中键": case "中键": case "滚轮按下": t = "Mouse2"; break;
                case "空格": case "空格键": t = "Space"; break;
                case "回车": case "回车键": case "确认键": t = "Return"; break;
                case "制表符": t = "Tab"; break;
                case "波浪键": case "`": case "~": t = "BackQuote"; break;
                case "左ctrl": case "左控制键": t = "LeftControl"; break;
                case "右ctrl": t = "RightControl"; break;
                case "左shift": t = "LeftShift"; break;
                case "右shift": t = "RightShift"; break;
                case "左alt": t = "LeftAlt"; break;
                case "右alt": t = "RightAlt"; break;
            }
            try { kc = (KeyCode)Enum.Parse(typeof(KeyCode), t, true); return kc != KeyCode.None; }
            catch { }
            return false;
        }

        // ---------------- 读键 ----------------
        static bool IsMouse(KeyCode k) { return k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6; }

        public static bool Held(KeyCode[] kk)
        {
            if (kk == null) return false;
            for (int i = 0; i < kk.Length; i++)
            {
                if (IsMouse(kk[i])) { if (Input.GetMouseButton((int)kk[i] - (int)KeyCode.Mouse0)) return true; }
                else if (Input.GetKey(kk[i])) return true;
            }
            return false;
        }

        public static bool Down(KeyCode[] kk)
        {
            if (kk == null) return false;
            for (int i = 0; i < kk.Length; i++)
            {
                if (IsMouse(kk[i])) { if (Input.GetMouseButtonDown((int)kk[i] - (int)KeyCode.Mouse0)) return true; }
                else if (Input.GetKeyDown(kk[i])) return true;
            }
            return false;
        }

        // ---------------- 默认配置 (首次运行自动生成) ----------------
        const string DefaultIni =
@"; ============================================================
;  CFZ Offline Mod — 键位表
;  改完存盘后, 进游戏按 F10 立即生效 (不用重启)
; ============================================================
;
; 【为什么在这里配】
;   游戏自己的 Option.Key.FPS 整张是空的 (实测 map.Count=0, GetKey 全 None,
;   连 GetDefaultKey 也是 None, SetDefault 也救不回来) → InputTable 派发不出任何
;   按钮事件 → 开火/跳跃/换武器/换弹/蹲 全部失灵。
;   本 mod 的按键旁路改成直接读物理键, 并派发与游戏表完全一致的 GameEvent, 键位配在下面。
;
; 【写法】
;   格式: 名字 = 键名        键名不区分大小写, 中英文名都认
;   可用键名 (Unity KeyCode):
;     字母/数字   A-Z / Alpha0-Alpha9(小键盘上方) / Keypad0-Keypad9(小键盘)
;     功能键      F1-F12 / Tab / Escape / Space / Return / BackQuote / CapsLock
;     修饰键      LeftShift / RightShift / LeftControl / RightControl / LeftAlt / RightAlt
;     鼠标        Mouse0(左键) Mouse1(右键) Mouse2(中键) Mouse3-Mouse6
;     中文别名    鼠标左键 / 鼠标右键 / 空格 / 回车 / 左ctrl / 右ctrl / 左shift / 波浪键
;     也可以直接写数字 (KeyCode 的整数值)
;   一个动作可绑多个键, 用逗号分隔:   蹲下 = LeftControl, RightControl
;   ★留空 = 禁用该动作
;   ★写错的键名会被忽略, 并在 output_log.txt 里打出警告
;
; 【不在这里配的】
;   滚轮换武器 —— 引擎原生处理, 与本表无关:
;     InputManager.cs:459 直接读 Mouse ScrollWheel → Input_UpdateMouse
;     → PlayerInputEvent.cs:122-134 派发 TryChangeWeaponSlotUpper/Lower
;   拆包(FPS_Defuse) / 纳米变身(FPS_Nano*) / 换英雄(FPS_ChangeHero)
;     在 AI_Tutorial 里用不到, 未接入。

[按键]

; ---------- 按住不放型 ----------
; 开火 (自动武器按住可连发)
开火            = Mouse0
; 特殊攻击 / 右键开镜
副开火          = Mouse1
; 蹲下
蹲下            = LeftControl, RightControl
; 静步走
静步            = LeftShift, RightShift
; 记分板 (按住显示, 松开隐藏)
记分板          = Tab
; 战术地图 (按住显示, 松开隐藏)
战术地图        = M

; ---------- 按一下型 ----------
跳跃            = Space
换弹            = R
主武器          = Alpha1, Keypad1
副武器          = Alpha2, Keypad2
近战            = Alpha3, Keypad3
投掷            = Alpha4, Keypad4
功能            = Alpha5, Keypad5
第六槽          = Alpha6, Keypad6
上一把          = Q
下一把          = E
菜单            = Escape
装备背包        = B
聊天            = T
无线电1         = Z
无线电2         = X
无线电3         = C
丢弃武器        = G
武器详情        = V

; ---------- 移动 (同时决定 WASD 旁路) ----------
前进            = W
后退            = S
左移            = A
右移            = D

; ---------- 鼠标灵敏度 (1~100) ----------
; 开镜灵敏度 = 50
;   开镜时鼠标增量 × (值/100)。离线游戏没载入用户设置, 这个值恒 0 → 开镜后完全转不动,
;   mod 会在每回合开始时按这里设置。留空 = 只在为 0 时自动补 50 (中性)。
; 鼠标灵敏度 = (留空 = 不动)
;   平时不开镜的鼠标手感 (游戏公式: 0.127 + 0.5 × 值/100, 值越大越快)。
;   ★留空 = 完全不动 (保持你当前的手感); 写了就按写的来。
开镜灵敏度      = 50
鼠标灵敏度      =
;
; ============================================================
;  【角色 / 武器】离线进图时, 你用哪个角色、带什么枪
;  改完存盘 → 按 F10 重载 (Debug 版还可按 F5 打印解析结果)
;  ★可用角色/武器的完整名单(带 weaponID/itemID) → 同目录 Helper.md
;    这个文件由 mod 运行时自动生成并刷新(按 F10 立即刷新), 会跟着游戏数据版本同步
; ============================================================
;
; 【为什么需要在这里配】
;   没有服务器, 进图时「我是谁、拿什么枪」这份数据是游戏自己硬编码造出来的:
;     AI_Tutorial_Loading.cs:39   CurrentCharacterType = CharacterType.SWAT   ← 角色写死 SWAT
;     AI_Tutorial_Loading.cs:69   i=0 M4A1 / i=1 DESERTEAGLE / i=2 KNIFE      ← 武器写死
;     AI_Tutorial_Loading.cs:82   PlayerUtil.AddEquipment(Equipment, BL, 30001) ← 30001 = SWAT 的 Item
;   本 mod 在它造完之后、LoadingUI._LoadPlayer 建玩家之前把这份数据改写掉
;   (LoadingUI 里 _LoadPlayer 之前隔着整张地图的加载 + 多次 yield, 时序安全)。
;
; 【写法】
;   角色 = 枚举名 或 游戏里的显示名 (全部候选见 Helper.md)
;          SWAT / OMOH / SAS / FOXHOWL / MOS / JNS / JON / ULP / MI6 / WHITEWOLF / ARIO / BLACK_RAVEN
;   队伍 = BL / GR   (选 GR 时 AI 会被换到 BL, 保证还有对手可打)
;   武器_主 / 武器_副 / 武器_近战 / 武器_投掷 = 武器名 (每个槽位的候选见 Helper.md)
;          ★留空 = 该槽不放武器;  写错 = 保留它自己的默认并打警告
;          ★名字取自游戏文本表 TextManager.GetWeaponName, 也可以直接写 weaponID 数字
;
[角色武器]
角色      = SWAT
队伍      = BL
武器_主   = M4A1
武器_副   = DESERTEAGLE
武器_近战 = KNIFE
武器_投掷 = M67GRENADE
; ---------- 备用背包 (进图后按 B 呼出面板, 点数字切换) ----------
;  背包2 ~ 背包7 = 逗号分隔的武器名; 武器进哪个槽由武器表决定(和 [敌人] 段写法一样)
;  没写的槽 = 这个背包不带那类武器; 整行留空/删掉 = 没有这个背包
背包2     = AK-47, DESERTEAGLE, KNIFE
;背包3     = AWM, DESERTEAGLE, KNIFE, M67GRENADE
;背包4     =
;
; ============================================================
;  【敌人 / AI 机器人】几个 AI、各用什么角色和枪
;  整段留空 = 完全沿用游戏默认 (4 个 AI, SWAT + M4A1/沙鹰/刀), 一个都不碰
;  ★每个角色/武器键都能写多个名字, 用逗号分隔 → 按 AI 顺序轮流分配 (写 2 个 = AI 交替用)
;  ★只写某个槽 = 只换那个槽, 其它槽保持游戏默认 (不会把 AI 的刀/沙鹰弄丢)
;  写法与 [角色武器] 段完全一样; 名字清单见 Helper.md
;
;  游戏默认 (想显式写出来就照抄):  角色=SWAT  武器_主=M4A1  武器_副=DESERTEAGLE
;                                  武器_近战=KNIFE   武器_投掷=(空)
;
[敌人]
; ---------- 数量 ----------
; 敌人(AI) 数量 —— 游戏默认 4, 允许 1~7
;   上限 7 是游戏「取名池」决定的 (BotMaxName=7 且要求互不重复), 填更大只会被夹到 7
;   填 >4 时地图刷点可能不够, mod 会自动借用合法刷点并在日志里提示 (不会崩)
数量      = 4
; 回合胜利需要的击杀数 (HUD 右上角 X/目标) —— 游戏默认 20, 最小 2
;   填 0 或 1 游戏会当成「按时间结束」的目标(GoalMaxCount=-1) → 一进游戏回合立刻结算, 所以会被夹到 2
;   填很大也没事: 600 秒(回合时间)到了就按当时比分结算
杀敌数    = 20
; ---------- 难度 ----------
; 预设档: 简单 / 普通(= 游戏原值) / 困难 / 自定义
;   简单 = 抖动 14,4     | 间隔 0.45s | 索敌 40m | 复活 6s | 速度 6
;   普通 = 抖动 7,2      | 间隔 0.2s  | 索敌 80m | 复活 3s | 速度 8   ← 游戏原值
;   困难 = 抖动 0.5,0.5  | 间隔 0.2s  | 索敌 80m | 复活 3s | 速度 8   (只收紧瞄准, 几乎不空枪)
;   ★下面这些键只要写了, 就以写的为准; 没写的键 = 用预设档的值★
;   游戏里 AI 的「技能」全是硬编码常量, mod 用 Harmony 把它们改成运行时读这里的值 → F10 改完立刻生效
难度      = 普通
; 瞄准随机抖动 (横向[,纵向], 世界坐标米数) —— AI 唯一的「命中率」, 0 = 百发百中, 越大越打不中
;   只写一个数 = 横纵一样; 它也决定「开火时的抖动」, 是难度的最大杠杆
瞄准抖动  = 7, 2
; 每次开火决策的间隔(秒) —— 越小打得越密 (0.02 约等于每秒 50 次决策)
开火间隔  = 0.2
; 索敌距离(米) / 球扫半径(米) —— 超出这个距离或半径就看不见你 (原版 80 / 6 几乎全图可见)
索敌距离  = 80
视野半径  = 6
; AI 死后复活延迟(秒) / 复活时血量 —— 原版 3 秒、写死 100 血(与角色原始血量无关)
复活时间  = 3
AI血量    = 100
; 移动速度 (0 = 站着不动) —— 进图创建 AI 时才读, 改完要重新进图
移动速度  = 8
; ---------- 角色 / 武器 ----------
角色      =
武器_主   =
武器_副   =
武器_近战 =
武器_投掷 =
;
; ============================================================
;  【地图】换图功能已移除 (2026-10-09) —— 恒用游戏默认教学图 Transportship_ren
;  原因: AI 教学模式的导航/巡逻数据只配套教学图 (写死 Transportship_Path),
;        换别的图后 bot 会按教学图坐标巡逻 → 掉坑, 需要大量补丁硬修, 得不偿失。
;  ini 里旧的『地图 / 敌人出生』配置已忽略, 留着不删也不影响。
; ============================================================
";
    }

    // ==================== ★角色 / 武器 (INI)★ ==================================================
    //
    //  【问题】离线没有服务器, 进图时"我是谁、拿什么枪"这份数据是游戏**自己硬编码**造出来的:
    //
    //     AI_Tutorial_Loading.SetModeInfo  (AI_Tutorial_Loading.cs:19-139)   ← 离线进图的数据源
    //        :22   myInven = GetData<MyInventory>()          (我们的伪造背包, EquipmentList 是空的)
    //        :39   eventParam_UserInfo.CurrentCharacterType = CharacterType.SWAT      ★ 角色写死 SWAT
    //        :46   循环 4 个武器槽: 先从 myInven.FindEquipmentList 取 → 取不到(num==0)就回退:
    //        :69      i=0 → ITEM_ID_M4A1      / WEAPON_ID_M4A1
    //        :73      i=1 → ITEM_ID_DESERTEAGLE / WEAPON_ID_DESERTEAGLE
    //        :77      i=2 → ITEM_ID_KNIFE     / WEAPON_ID_KNIFE                       ★ 武器写死
    //        :82   PlayerUtil.AddEquipment(Equipment, ETeamID.BL, 30001)              ★ 30001 = SWAT 的 Item
    //        :92   NetEvent.Param_LoadingStart.UserInfo.Add(eventParam_UserInfo)
    //        :116  再给 NPCCount 个 AI 造 UserInfo (队伍 GR, 也是 SWAT+M4A1)
    //        :137  DummySendEvent(GameEvent.Loading_Start, NetEvent.Param_LoadingStart)
    //
    //     之后 LoadingUI._Loading 协程把它变成玩家:
    //        LoadingUI.cs:142  Load<Object>(场景包) + while(!job.IsComplete) yield   ← 中间隔了整张地图
    //        LoadingUI.cs:187  _LoadPlayer(param.UserInfo[i])
    //        LoadingUI.cs:211-229 → EventParam_PlayerAdd { CharacterType = info.CurrentCharacterType,
    //                                                      WeaponBag = info.WeaponBag, Equipment = info.Equipment }
    //        → Player.Add → Data.OnCreate(CharacterType/WeaponBag) → 决定 QV身体 / PV手臂 / 手里那把枪
    //
    //  【做法】在 SetModeInfo **返回之后**(postfix)、_LoadPlayer 之前, 把 UserInfo 里"我"那一项整个重写。
    //         时序安全: 中间隔着场景异步加载 + 多次 yield (LoadingUI.cs:137/145/153/161)。
    //         同一份改写也在 LoadingUI.OnEvent_LoadingStart 之后再做一遍 (兜底, 覆盖 F7/F8 等其它入口)。
    //
    //  【名字 → ID】表里只有 index, 没有"按名字反查"的接口, 所以:
    //      武器名: ItemTable.GetAllWeaponID().NamesByID  (weaponID → TextManager.GetWeaponName(weaponID))
    //              → 拿到 weaponID 再 GetItemTableIndex(Weapon, weaponID) 得到 bag 需要的 ItemID
    //      角色名: CharacterType 枚举名, 或 TextManager.GetCharacterNameString((int)type) 的显示名
    //              → 拿到 CharacterType 再 GetItemTableIndex(Character, (int)type)  (= SWAT 的 30001 那种 ItemID)
    //      (DefineItemID / DefinedWeaponID 只是寥寥几个常量, 不能当完整名单用)
    //
    //  【注意】PlayerUtil.AddWeaponToBag 在 WeaponTable 里找不到该武器的弹药条目时会**静默跳过**
    //          (PlayerUtil.cs:492-493) → 这里用 Slots.Count 前后对比判断是否真的进包, 并打警告。

    public static class Loadout
    {
        public const string Sec = ModConfig.SecLoadout;
        public const string SecEnemy = ModConfig.SecEnemy;

        public static bool Ready = false;
        public static string Status = "未加载";

        static CharacterType _char = CharacterType.SWAT;
        static int _charItem = 0;
        static ETeamID _team = ETeamID.BL;
        static readonly int[] _item = new int[6];               // bag 里的 ItemID
        static readonly int[] _weapon = new int[6];             // 武器 weaponID
        static readonly string[] _raw = new string[6];          // ini 里写的原文 (日志用)
        static readonly string[] _rawAll = new string[6];       // 原始配置 (未解析)

        static readonly string[] _slotKey = new string[] { "武器_主", "武器_副", "武器_近战", "武器_投掷", "武器_5", "武器_6" };
        static readonly WEAPONSLOT[] _slotWant = new WEAPONSLOT[] {
            WEAPONSLOT.WEAPONSLOT_FIRST, WEAPONSLOT.WEAPONSLOT_SECOND, WEAPONSLOT.WEAPONSLOT_THIRD,
            WEAPONSLOT.WEAPONSLOT_FOURTH_1, WEAPONSLOT.WEAPONSLOT_FOURTH_2, WEAPONSLOT.WEAPONSLOT_FOURTH_3 };

        // ---- 背包2~7 (B 键呼出面板后切换) ----
        //   背包1 = 上面 4 个武器槽键; 背包2~7 各是一个 ini 键 "背包N", 值 = 逗号分隔的武器名。
        //   换袋链路: 选袋 → InGameUI.OnSelectWeaponBag → Network_SendChangeWeaponBag (要服务器回包)
        //   → 离线没人回, SendEventPrefix 里直接调 PlayerManager.RecvWeaponBagChange 完成。
        static readonly System.Collections.Generic.List<int[]> _bagItem =
            new System.Collections.Generic.List<int[]>();
        static readonly System.Collections.Generic.List<int[]> _bagWid =
            new System.Collections.Generic.List<int[]>();
        static readonly System.Collections.Generic.List<string[]> _bagRaw =
            new System.Collections.Generic.List<string[]>();
        static int _bagParseLog = 0;

        static int _applied = 0;
        static int _loadLog = 0;
        static int _eLoadLog = 0;

        // ---- 敌人 (AI) 配置 ----
        //   每个键都是"列表": 写多个名字(逗号/分号/竖线分隔), AI 按顺序轮流取 (k % Count)
        //   整段空 = 一个都不碰, AI 完全沿用游戏默认 (SWAT + M4A1/沙鹰/刀, 见 AI_Tutorial_Loading.cs:116-135)
        //   只配了某个槽 → 只覆盖那个槽; 未配置的槽从它原有的袋里搬过来, 保持游戏默认
        static readonly System.Collections.Generic.List<CharacterType> _eChar =
            new System.Collections.Generic.List<CharacterType>();
        static readonly System.Collections.Generic.List<int> _eCharItem =
            new System.Collections.Generic.List<int>();
        static readonly System.Collections.Generic.List<int>[] _eWid =
            new System.Collections.Generic.List<int>[] {
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>(),
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>(),
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>() };
        static readonly System.Collections.Generic.List<int>[] _eItem =
            new System.Collections.Generic.List<int>[] {
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>(),
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>(),
                new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>() };
        static readonly System.Collections.Generic.List<string>[] _eRaw =
            new System.Collections.Generic.List<string>[] {
                new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(),
                new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(),
                new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>() };

        // ---- 数量 / 杀敌目标 ----
        //   数量   = 敌人(AI) 总数 (写进 InGameMode_AI_Tutorial.NPCCount, 必须在 SetModeInfo 之前设好)
        //            ★上限 7★: 游戏取名是 Random.Range(10001, 10001+BotMaxName), BotMaxName=7 且要求互不重复,
        //            NPCCount>7 时那个 while(true) 永远凑不齐 → SetModeInfo 卡死
        //   杀敌数 = 回合胜利需要的击杀数 (写进 InGameMode_AI_Tutorial.KillCount, 同样要抢在 SetModeInfo 之前)
        //            ★最小 2★: InGameUtil.CaculateGoalType 里 0/1 会走 GoalType.Time → GoalMaxCount=-1
        //            → 而 InGameMode_AI_Tutorial.OnUpdateRoundScore 判的是 RoundScore >= GoalMaxCount
        //              → 0 >= -1 恒真 → 一进游戏回合马上结束
        static int _aiCount = 4;
        static int _killGoal = 20;
        static int _aiLog = 0;

        // ---- 难度: 教学 AI 的"技能"全是硬编码常量, 这里存 ini 里的值 ----
        //   由 CfzOfflineDriver 的 Transpiler 把原方法里的常量换成"运行时调这些 getter",
        //   所以按 F10 改完立刻生效(不用重启); 唯一例外是 移动速度(在 NpcPathFinder.Start 里只读一次,
        //   改完要等这个 bot 下次复活/进图才生效 —— 其实 Start 只在创建时跑一次, 复活不重跑)。
        //   ★这些字段只能在 ini 解析线程写、游戏线程读, float 是原子的 ✓ (不做锁)
        public static float AimJitterX = 7f;        // AI_TutorialPlayer.SearchTarget + FireStarting.Update
        public static float AimJitterY = 2f;
        public static float FireInterval = 0.2f;   // FireStarting.Update
        public static float SightRange = 80f;      // AI_TutorialHitCheck.CaculateHit (球扫距离 + 视线射线距离)
        public static float SightRadius = 6f;      // 同上 (球扫半径)
        public static float RespawnDelay = 3f;     // AI_TutorialPlayerState_die.Update
        public static int BotRespawnHp = 100;      // 同上 (原版写死 100, 与角色原始血量无关)
        public static float MoveSpeed = 8f;        // NpcPathFinder.Start
        public static string DifStatus = "未加载";

        // ---- 地图: [地图] 地图 = 场景名 或 MapID ----
        //   AI_Tutorial_Loading.cs:24 把 NetEvent.Param_LoadingStart.MapIndex 写死成 1 (= Transportship_ren)
        //   LoadingUI.cs:141          bundleName = MapTable.GetSceneName(param.MapIndex) → 加载该场景包
        //   所以我们在 SetModeInfo 的 **postfix** 里把它改掉:
        //   那时原方法已跑完(它刚写完 1)、而 LoadingUI 协程还没开始读 → 时序安全。
        public static string MapRaw = "";                       // ini 原文 (空 = 用游戏默认)
        public static int MapId = 1;                            // 生效的 MapIndex
        public static string MapScene = "Transportship_ren";    // 生效的场景名
        public static string MapStatus = "游戏默认 (MapIndex 1 = Transportship_ren)";
        public static bool MapResolved = false;                 // MapRaw 是否已成功解析 (false = 进图时再解析一次, 兜底)
        public static string LoadedSceneName = "";              // 实际加载的场景包名 (场景接管那里写进来, 自检用)
        public static bool MapSpawnNearMe = false;              // [地图] 敌人出生 = 我附近 → bot 摆玩家出生点旁一圈
        public static string MapSpawnStatus = null;             // 上面那行的说明文字 (日志/自检里显示)
        static int _mLoadLog = 0;
        static string _mapChecked = null;                       // 自检过的场景 (同一场景只打一次)

        // ↓ 这几个是 Transpiler 注进去的"取值口", 名字和顺序别乱改 (改了要同步 Driver 里的 getter 名)
        public static float AimMinX() { return -AimJitterX; }
        public static float AimMaxX() { return AimJitterX; }
        public static float AimMinY() { return -AimJitterY; }
        public static float AimMaxY() { return AimJitterY; }
        public static float GetFireInterval() { return FireInterval; }
        public static float GetSightRange() { return SightRange; }
        public static float GetSightRadius() { return SightRadius; }
        public static float GetRespawnDelay() { return RespawnDelay; }
        public static int GetBotHp() { return BotRespawnHp; }
        public static float GetMoveSpeed() { return MoveSpeed; }

        // ★ [地图] Transpiler 注进 AI_Tutorial_Loading.SetModeInfo 的取值口 (名字别改, 要同步 Patcher 里的 getter)
        public static int GetMapId()
        {
            // 兜底: ini 解析那一刻 MapTable 可能还没就绪(Load 早退) → 进图这一刻数据表肯定好了
            try { if (!MapResolved && !string.IsNullOrEmpty(MapRaw)) LoadMap(); } catch { }
            return MapId;
        }

        public static string EnemyStatus = "未加载";

        // ---------------- 读取 ini (解析需要数据表, 数据表没就绪就只记原文) ----------------
        public static void Load()
        {
            try
            {
                Ready = false;
                if (!ModConfig.Loaded) ModConfig.EnsureAndLoad();     // 防呆: 没读 ini 就解析会得到空配置 → 玩家空手进图
                // ★ 数量 / 杀敌数: 一读 ini 就解析 (与数据表无关) —— 这样即使后面因为数据表没就绪提前 return,
                //    NPCCount / KillCount 也已经武装好, SetModeInfo 抄走时不会退回默认 4 / 20
                _aiCount = ParseInt(ModConfig.SV(SecEnemy, "4", new string[] { "数量", "敌人数量", "AI数量", "BOT数量" }),
                                    4, 1, 7, "数量", "游戏名字池只有 7 个名字(BotMaxName), 超过会让取名死循环");
                _killGoal = ParseInt(ModConfig.SV(SecEnemy, "20",
                                    new string[] { "杀敌数", "目标杀敌数", "击杀目标", "KillCount" }),
                                    20, 2, 9999, "杀敌数", "游戏把 0/1 当成「按时间结束」的目标(GoalMaxCount=-1), 会让回合立刻结算");

                // ★ 难度: 先取预设(简单/普通/困难), 再让显式键覆盖 —— 显式键缺省时 = 预设值
                float dAx, dAy, dFi, dSight, dRad, dRsp, dSpd;
                int dHp;
                DifficultyPreset(ModConfig.SV(SecEnemy, "普通", new string[] { "难度", "AI难度" }),
                                 out dAx, out dAy, out dFi, out dSight, out dRad, out dRsp, out dHp, out dSpd);

                // 瞄准抖动 = 横向[, 纵向]   (只写一个数 = 横纵一样; 0 = 百发百中)
                var jit = SplitList(ModConfig.SV(SecEnemy, "", new string[] { "瞄准抖动", "瞄准误差" }));
                float jx = dAx, jy = dAy;
                if (jit.Count > 0)
                {
                    jx = ParseF(jit[0], dAx, 0f, 90f, "瞄准抖动", "单位是世界坐标米数, 90 已经等于完全打不中");
                    jy = (jit.Count > 1) ? ParseF(jit[1], dAy, 0f, 90f, "瞄准抖动(纵向)", "同上")
                                         : (jit[0].Trim() == "0" ? 0f : jx);
                }
                AimJitterX = jx; AimJitterY = jy;
                FireInterval = ParseF(ModConfig.SV(SecEnemy, "", new string[] { "开火间隔" }),
                                      dFi, 0.02f, 10f, "开火间隔", "0.02 秒 ≈ 每秒 50 次开火决策, 再小没有意义");
                SightRange = ParseF(ModConfig.SV(SecEnemy, "", new string[] { "索敌距离", "视距" }),
                                    dSight, 1f, 300f, "索敌距离", "超出这个距离 AI 看不见你");
                SightRadius = ParseF(ModConfig.SV(SecEnemy, "", new string[] { "视野半径", "索敌半径" }),
                                     dRad, 0.1f, 50f, "视野半径", "索敌是「球扫」, 半径越大越容易「擦到」你");
                RespawnDelay = ParseF(ModConfig.SV(SecEnemy, "", new string[] { "复活时间" }),
                                      dRsp, 0.1f, 120f, "复活时间", "AI 死后多久复活");
                BotRespawnHp = ParseInt(ModConfig.SV(SecEnemy, "", new string[] { "AI血量", "复活血量" }),
                                        dHp, 1, 100000, "AI血量", "AI 复活时的血量(原版写死 100)");
                MoveSpeed = ParseF(ModConfig.SV(SecEnemy, "", new string[] { "移动速度" }),
                                   dSpd, 0f, 30f, "移动速度", "0 = 站着不动; 进图后创建 AI 时才读, 改完要重新进图");
                DifStatus = ModConfig.SV(SecEnemy, "普通", new string[] { "难度", "AI难度" }) +
                            "(抖动 " + AimJitterX + "," + AimJitterY + " 间隔 " + FireInterval + "s 索敌 " + SightRange +
                            "m/半径 " + SightRadius + "m 复活 " + RespawnDelay + "s 血量 " + BotRespawnHp +
                            " 速度 " + MoveSpeed + ")";
                if (!ModConfig.HasSection(Sec))
                {
                    Status = "ini 里没有 [" + Sec + "] 段 → 这次不改写, 用游戏默认角色/武器";
                    Debug.LogWarning("[CFZ-Offline][INI] " + Status);
                    return;
                }
                _rawAll[0] = ModConfig.SV(Sec, "SWAT", new string[] { "角色" });
                string team = ModConfig.SV(Sec, "BL", new string[] { "队伍", "阵营" });
                for (int i = 0; i < 4; i++) _raw[i] = ModConfig.SV(Sec, "", new string[] { _slotKey[i] });


                // 角色 (枚举名 / 显示名 / 数字, 不需要数据表)
                CharacterType ct;
                if (TryCharacter(_rawAll[0], out ct)) _char = ct;
                else
                {
                    _char = CharacterType.SWAT;
                    Debug.LogWarning("[CFZ-Offline][INI][角色武器] 角色 '" + _rawAll[0] + "' 认不出来 → 用 SWAT. 可选: " + CharacterCandidates());
                }

                // 队伍
                string tn = (team == null ? "BL" : team.Trim().ToUpperInvariant());
                _team = (tn == "GR" || tn == "2") ? ETeamID.GR : ETeamID.BL;

                // 数据表要等游戏加载完才有 —— 没有就下次 Apply 时再来
                var it = CFW.DataTable.DataTableManager.ItemTable;
                if (it == null) { Status = "已读配置, 数据表未就绪(等进图/按 F10 再解析)"; return; }

                // 角色 ItemID
                _charItem = 0;
                try { _charItem = it.GetItemTableIndex(CFW.DataTable.Header.DataTableItemType.Character, (int)_char); } catch { }
                if (_charItem == 0 && _char != CharacterType.SWAT)
                {
                    Debug.LogWarning("[CFZ-Offline][INI][角色武器] 角色 " + _char + " 在 ItemTable 里找不到 Item 索引 → 退回 SWAT");
                    _char = CharacterType.SWAT;
                    try { _charItem = it.GetItemTableIndex(CFW.DataTable.Header.DataTableItemType.Character, (int)_char); } catch { }
                }
                // 数据表对象在, 但条目还是空的(启动早期) → 判为"没就绪", Apply/按 F10 时再来
                if (_charItem == 0)
                {
                    Ready = false;
                    Status = "数据表还没加载完, 稍后自动重试 (Apply / F10)";
                    return;
                }

                // 武器 4 个槽
                for (int i = 0; i < 6; i++) { _item[i] = 0; _weapon[i] = 0; }
                int okN = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (string.IsNullOrEmpty(_raw[i])) continue;      // 留空 = 不放
                    int wid, iid;
                    if (TryWeapon(_raw[i], _slotWant[i], out wid, out iid))
                    {
                        _weapon[i] = wid; _item[i] = iid; okN++;
                    }
                    else
                    {
                        Debug.LogWarning("[CFZ-Offline][INI][角色武器] " + _slotKey[i] + "='" + _raw[i] +
                                         "' 找不到对应武器 → 该槽保留游戏默认. 该槽候选: " + WeaponCandidates(_slotWant[i], 20));
                    }
                }

                // ---- 背包2~7: 每个键一个备用武器袋, 进图后按 B 切换 ----
                _bagItem.Clear(); _bagWid.Clear(); _bagRaw.Clear();
                for (int b = 2; b <= 7; b++)
                {
                    string src = ModConfig.SV(Sec, "", new string[] { "背包" + b });
                    if (string.IsNullOrEmpty(src) || src.Trim().Length == 0) continue;
                    var names = SplitList(src);
                    if (names.Count == 0) continue;
                    if (names.Count > 6)
                        Debug.LogWarning("[CFZ-Offline][INI][角色武器] 背包" + b + " 写了 " + names.Count +
                                         " 把, 一袋最多 6 把 → 只取前 6");
                    int cnt = Math.Min(names.Count, 6);
                    var wid = new int[cnt]; var item = new int[cnt]; var raw = new string[cnt];
                    int ok = 0;
                    for (int s = 0; s < cnt; s++)
                    {
                        raw[s] = names[s];
                        int w, ii;
                        if (ResolveOneWeapon(names[s], WEAPONSLOT.WEAPONSLOT_NONE, out w, out ii))
                        { wid[s] = w; item[s] = ii; ok++; }
                        else
                            Debug.LogWarning("[CFZ-Offline][INI][角色武器] 背包" + b + " 的 '" + names[s] +
                                             "' 找不到对应武器 → 跳过这把 (候选见 Helper.md)");
                    }
                    if (ok == 0) { Debug.LogWarning("[CFZ-Offline][INI][角色武器] 背包" + b + " 一把都没解析成功 → 整袋忽略"); continue; }
                    _bagWid.Add(wid); _bagItem.Add(item); _bagRaw.Add(raw);
                    if (_bagParseLog++ < 8)
                        Debug.LogWarning("[CFZ-Offline][INI][角色武器] 背包" + b + " 解析成功 " + ok + "/" + cnt + " 把");
                }

                // ---- 敌人 (AI): 与上面同一套解析, 不过每个键是"列表" ----
                _eChar.Clear(); _eCharItem.Clear();
                for (int s = 0; s < 6; s++) { _eWid[s].Clear(); _eItem[s].Clear(); _eRaw[s].Clear(); }

                // (数量已在上面的 Load 开头解析)

                if (ModConfig.HasSection(SecEnemy))
                {
                    foreach (string one in SplitList(ModConfig.SV(SecEnemy, "", "角色")))
                    {
                        CharacterType ect;
                        if (!TryCharacter(one, out ect))
                        {
                            Debug.LogWarning("[CFZ-Offline][INI][敌人] 角色 '" + one + "' 认不出来 → 忽略(该 AI 用游戏默认 SWAT). 可选: " + CharacterCandidates());
                            continue;
                        }
                        int eit = 0;
                        try { eit = it.GetItemTableIndex(CFW.DataTable.Header.DataTableItemType.Character, (int)ect); } catch { }
                        if (eit <= 0)
                        {
                            Debug.LogWarning("[CFZ-Offline][INI][敌人] 角色 " + ect + " 在 ItemTable 里找不到 Item 索引 → 忽略");
                            continue;
                        }
                        _eChar.Add(ect); _eCharItem.Add(eit);
                    }
                    for (int s = 0; s < 4; s++)
                    {
                        foreach (string one in SplitList(ModConfig.SV(SecEnemy, "", _slotKey[s])))
                        {
                            int wid, iid;
                            if (TryWeapon(one, _slotWant[s], out wid, out iid))
                            { _eWid[s].Add(wid); _eItem[s].Add(iid); _eRaw[s].Add(one); }
                            else
                                Debug.LogWarning("[CFZ-Offline][INI][敌人] " + _slotKey[s] + "='" + one +
                                                 "' 找不到对应武器 → 忽略. 该槽候选: " + WeaponCandidates(_slotWant[s], 20));
                        }
                    }
                }
                EnemyStatus = EnemyText();
                LoadMap();                                       // ★ [地图] 段 (在 SetModeInfo 的 postfix 里生效)
                ArmRules();                                      // ★ 把"数量/杀敌数"写进游戏 (SetModeInfo 马上就抄走)

                Ready = true;
                _ver++;                                          // ★ 配置版本+1 → 名单文档下次会自动重写
                Status = "角色=" + _char + "(Item " + _charItem + ") 队伍=" + _team + " 武器=" + okN + "/4 [" +
                         SlotText(0) + " | " + SlotText(1) + " | " + SlotText(2) + " | " + SlotText(3) + "]";
                if (_loadLog++ < 3) Debug.LogWarning("[CFZ-Offline][INI][角色武器] 配置已解析: " + Status);
                if (_eLoadLog++ < 3) Debug.LogWarning("[CFZ-Offline][INI][敌人] 配置已解析: " + EnemyStatus);
            }
            catch (Exception e)
            {
                Status = "解析失败: " + e.Message;
                Debug.LogError("[CFZ-Offline][INI][角色武器] " + Status);
            }
        }

        static string SlotText(int i)
        {
            if (_item[i] <= 0) return _slotKey[i] + "=空";
            return _slotKey[i] + "=" + _raw[i] + "(weaponID " + _weapon[i] + "/item " + _item[i] + ")";
        }

        // ---------------- 数量 / 杀敌目标 ----------------
        //  数量   → InGameMode_AI_Tutorial.NPCCount —— 它读这个值造 bot (AI_Tutorial_Loading.cs:93/116)
        //           和刷 bot (InGameMode_AI_Tutorial.cs:186)。
        //           上限 7 是硬限制: 取名用 Random.Range(10001, 10001+BotMaxName), BotMaxName=7 且要互不重复。
        //  杀敌数 → InGameMode_AI_Tutorial.KillCount —— AI_Tutorial_Loading.cs:26 抄进
        //           NetEvent.Param_LoadingStart.KillCount → InGame.cs:248 传给 InGameModeBase.Create
        //           → InGameModeBase.cs:99 GoalMaxCount = InGameUtil.CaculateGoalType(killCount, roundCount, ...)
        //           → InGameMode_AI_Tutorial.cs:74/78 两边比分谁先到 GoalMaxCount 谁赢。
        //  两个都必须在 SetModeInfo 跑**之前**设好 —— 它在自己体内就把这两个值抄走了。
        public static void ArmRules()
        {
            try
            {
                int n = _aiCount < 1 ? 1 : (_aiCount > 7 ? 7 : _aiCount);
                int k = _killGoal < 2 ? 2 : _killGoal;
                bool changed = InGameMode_AI_Tutorial.NPCCount != n || InGameMode_AI_Tutorial.KillCount != k;
                InGameMode_AI_Tutorial.NPCCount = n;
                InGameMode_AI_Tutorial.KillCount = k;
                if (changed && _aiLog++ < 4)
                    Debug.LogWarning("[CFZ-Offline][敌人] 敌人数量 = " + n + " 个 (游戏默认 4), 杀敌目标 = " + k + " (游戏默认 20)");
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][敌人] 设置 NPCCount/KillCount 失败: " + e.Message); }
        }

        // 取整数并夹到 [min,max]; 越界打警告 (键名与原因由调用方给)
        static int ParseInt(string s, int def, int min, int max, string key, string why)
        {
            int v;
            if (string.IsNullOrEmpty(s) || !int.TryParse(s.Trim(), out v)) return def;
            if (v < min || v > max)
            {
                int c = v < min ? min : max;
                Debug.LogWarning("[CFZ-Offline][INI][敌人] " + key + " = " + v + " 超出范围 [" + min + ".." + max +
                                 "] → 夹到 " + c + " (" + why + ")");
                return c;
            }
            return v;
        }

        // 取浮点并夹到 [min,max]; 越界/写错打警告并回退 (键名与原因由调用方给)
        static float ParseF(string s, float def, float min, float max, string key, string why)
        {
            float v;
            if (string.IsNullOrEmpty(s) ||
                !float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v))
                return def;
            if (v < min || v > max)
            {
                float c = v < min ? min : max;
                Debug.LogWarning("[CFZ-Offline][INI][敌人] " + key + " = " + v + " 超出范围 [" + min + ".." + max +
                                 "] → 夹到 " + c + " (" + why + ")");
                return c;
            }
            return v;
        }

        // 难度预设: 简单 / 普通(= 游戏原值) / 困难; 认不出来的名字一律当"普通"
        //   这些值就是游戏里那几个硬编码常量的原值 (见 CfzOfflineDriver 的 [敌人难度] 注释)
        //   简单 = 各项都放松 | 普通 = 游戏原值 | 困难 = 普通, 只把 瞄准抖动 收到 0.5, 0.5 (几乎不空枪)
        static void DifficultyPreset(string name, out float ax, out float ay, out float fi,
                                     out float sight, out float rad, out float rsp, out int hp, out float spd)
        {
            string n = (name == null) ? "" : name.Trim().ToLowerInvariant();
            if (n == "简单" || n == "easy" || n == "低")
            { ax = 14f; ay = 4f; fi = 0.45f; sight = 40f; rad = 3f; rsp = 6f; hp = 100; spd = 6f; return; }
            if (n == "困难" || n == "hard" || n == "高")
            { ax = 0.5f; ay = 0.5f; fi = 0.2f; sight = 80f; rad = 6f; rsp = 3f; hp = 100; spd = 8f; return; }
            // 普通 / 自定义 / 认不出来 → 游戏原值 (写"自定义"只是表明下面要手动配)
            ax = 7f; ay = 2f; fi = 0.2f; sight = 80f; rad = 6f; rsp = 3f; hp = 100; spd = 8f;
        }

        // ---------------- 地图 [地图] 段 ----------------
        //   把 ini 的"地图"解析成 MapIndex: 写数字 = 直接当 MapID; 写名字 = 去 MapTable 里按场景名找。
        //   认不出来 / 数据表没就绪 → 回退游戏默认(1), 只打警告, 绝不中断进图。
        public static void LoadMap()
        {
            // ★换图功能已按用户要求移除★ (2026-10-09)
            //   恒用游戏默认 MapIndex 1 = Transportship_ren(教学图)。ini 里旧的『地图/敌人出生』配置一律忽略。
            //   原因: AI 教学模式的导航/巡逻数据写死为 Transportship_Path (InGameMode_AI_Tutorial.cs:41),
            //   换别的图后 bot 仍按教学图坐标(y≈-28)巡逻 → 掉坑, 刷点也要靠一堆补丁硬修, 得不偿失。
            MapId = 1;
            MapScene = SceneNameOf(1);
            MapStatus = "换图已移除 (恒默认 " + MapScene + ") | 敌人出生=地图刷点";
            MapSpawnNearMe = false;
            MapSpawnStatus = "地图刷点";
            MapResolved = true;
            if (_mLoadLog++ < 3)
                Debug.LogWarning("[CFZ-Offline][INI][地图] " + MapStatus + " (ini 里旧的『地图/敌人出生』配置已忽略)");
        }

        static string SceneNameOf(int id)
        {
            try
            {
                string s = CFW.DataTable.DataTableManager.MapTable.GetSceneName(id);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return (id == 1) ? "Transportship_ren" : ("MapID " + id);
        }

        // 只留字母数字并转小写 → 让 "Transportship_ren" / "transportshipren" 都能匹配
        static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (c != '_' && c != '-' && c != ' ') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // >0 = 解析到的 MapID; -1 = 用游戏默认 (note 里写原因, 会打进日志/状态)
        static int ResolveMapId(string raw, out string note)
        {
            note = "";
            if (string.IsNullOrEmpty(raw)) return -1;
            try
            {
                var mt = CFW.DataTable.DataTableManager.MapTable;
                if (mt == null) { note = "MapTable 还没加载完 → 这次用默认"; return -1; }
                var ids = mt.GetMapIDs();
                if (ids == null || ids.Count == 0) { note = "MapTable 是空的 → 这次用默认"; return -1; }

                string w = raw.Trim();
                int num;
                if (int.TryParse(w, out num))
                {
                    for (int i = 0; i < ids.Count; i++)
                        if (ids[i] == num) { note = "按 MapID"; return num; }
                    note = "MapTable 里没有 MapID " + num;
                    return -1;
                }

                string wn = NormName(w);
                int partial = -1; string partialSc = null;
                for (int i = 0; i < ids.Count; i++)
                {
                    string sc = mt.GetSceneName(ids[i]);
                    if (string.IsNullOrEmpty(sc)) continue;
                    if (string.Equals(sc, w, StringComparison.OrdinalIgnoreCase) || NormName(sc) == wn)
                    { note = "按场景名"; return ids[i]; }
                    if (partial < 0 && wn.Length >= 3 && NormName(sc).StartsWith(wn))
                    { partial = ids[i]; partialSc = sc; }
                }
                if (partial > 0) { note = "模糊匹配到场景 " + partialSc; return partial; }
                note = "认不出来 '" + w + "' (照抄场景名, 或写 MapID 数字)";
                return -1;
            }
            catch (Exception e) { note = "解析异常: " + e.Message; return -1; }
        }

        // ★ 把地图写进 NetEvent.Param_LoadingStart (只在 SetModeInfo 的 postfix 里调: 原方法刚写完 1, LoadingUI 还没读)
        public static void ApplyMap()
        {
            try
            {
                // 兜底: ini 解析那一刻 MapTable 可能还没就绪(Load 会早退) → 此刻在进图流程里, 数据表肯定好了
                if (!MapResolved && !string.IsNullOrEmpty(MapRaw)) LoadMap();

                int old = CFW.Network.NetEvent.Param_LoadingStart.MapIndex;
                CFW.Network.NetEvent.Param_LoadingStart.MapIndex = MapId;
                Debug.Log("[CFZ-Offline][地图] Param_LoadingStart.MapIndex: " + old + " → " + MapId +
                          " (" + MapScene + ")  " + MapStatus);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][地图] 写 MapIndex 失败: " + e.Message); }
        }

        // ★ 换图自检 (进图后打一次): 场景名对不对 + bot 走位依赖的 targetList / A* 导航图在不在
        public static void MapSelfCheck()
        {
            try
            {
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                string got = LoadedSceneName;                    // 场景接管那里记的"真正加载了哪张图"
                string key = (got.Length > 0) ? got : sc;        // 换图后要能再打一次 → 用场景包名做键
                if (key == _mapChecked) return;
                _mapChecked = key;

                GameObject tl = GameObject.Find("targetList");
                int tlN = (tl == null) ? -1 : tl.transform.childCount;
                // A* 的 AstarPath 在**全局命名空间**(不是 Pathfinding) → 用按名字找类型, 避免编译期耦合
                bool astar = false;
                try
                {
                    Type tAst = CfzOfflineDriver.FindTypeAnywhere("AstarPath");
                    if (tAst != null) astar = (UnityEngine.Object.FindObjectOfType(tAst) != null);
                }
                catch { }

                // ★ 一致性检查: 配置想说去哪张图, 实际加载的是哪张
                bool mismatch = (got.Length > 0 && MapScene.Length > 0 &&
                                 NormName(got).IndexOf(NormName(MapScene)) < 0);
                if (mismatch)
                    Debug.LogError("[CFZ-Offline][地图] ★不一致★ 配置='" + MapScene + "' 但实际加载的场景包='" + got +
                                   "' → MapIndex 没生效(贴日志给我)");

                Debug.Log("[CFZ-Offline][地图] 自检: 实际场景包='" + got + "' (ActiveScene='" + sc + "') | " + MapStatus +
                          " | targetList=" + (tl == null ? "★不存在★(bot 不会走动, 只原地转身开枪)" : tlN + " 个子点(bot 会巡逻)") +
                          " | A*导航图=" + (astar ? "有" : "★无★(bot 寻路会失败)") +
                          " —— 这两项任一为★ 只影响 bot 走位, 不影响开枪/索敌");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][地图] 自检异常: " + e.Message); }
        }

        static string EnemyText()
        {
            try
            {
                var sb = new System.Text.StringBuilder("数量=" + _aiCount + " 杀敌目标=" + _killGoal + " 难度=" + DifStatus);
                sb.Append(" | 角色=");
                if (_eChar.Count == 0) sb.Append("默认");
                else for (int i = 0; i < _eChar.Count; i++) { if (i > 0) sb.Append(','); sb.Append(_eChar[i]); }
                for (int s = 0; s < 4; s++)
                {
                    sb.Append(" | ").Append(_slotKey[s]).Append('=');
                    if (_eWid[s].Count == 0) { sb.Append("默认"); continue; }
                    for (int i = 0; i < _eRaw[s].Count; i++) { if (i > 0) sb.Append(','); sb.Append(_eRaw[s][i]); }
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        // ---------------- 写进"进图数据" ----------------
        public static void Apply()
        {
            try
            {
                if (!Ready) Load();                                  // 这次进图才解析得出来 (数据表此刻肯定好了)
                if (!Ready)
                {
                    // ★ 解析没成功就**什么都别动** —— 否则下面的 Bags.Clear() 会把玩家原有武器清空
                    Debug.LogWarning("[CFZ-Offline][角色武器] 配置没解析成功(" + Status + ") → 这次不改写, 保留游戏默认");
                    return;
                }
                var start = CFW.Network.NetEvent.Param_LoadingStart;
                if (start == null || start.UserInfo == null || start.UserInfo.Count == 0)
                {
                    if (_applied == 0) Debug.LogWarning("[CFZ-Offline][角色武器] 进图数据 UserInfo 为空, 这次不改写");
                    return;
                }

                int mine = 0, bots = 0, flip = 0;
                for (int i = 0; i < start.UserInfo.Count; i++)
                {
                    var u = start.UserInfo[i];
                    if (u == null) continue;

                    if (!u.IsMyPlayer)
                    {
                        // 敌人: 永远和我对立 (我 BL → AI GR / 我 GR → AI BL), 否则两边同队没得打。
                        // SlotIndex 的奇偶必须跟着队伍走 (奇=GR, 偶=BL, 见 NetClient.CalculateTeam:1943)
                        ETeamID eTeam = (_team == ETeamID.GR) ? ETeamID.BL : ETeamID.GR;
                        if (u.Team != eTeam) flip++;
                        u.Team = eTeam;
                        u.SlotIndex = (eTeam == ETeamID.GR) ? (bots * 2 + 1) : (bots * 2 + 2);
                        ApplyEnemy(u, bots, eTeam);        // ★ 按 [敌人] 段改角色/武器 (没配就一个都不碰)
                        bots++;
                        continue;
                    }

                    u.CurrentCharacterType = _char;
                    u.Team = _team;
                    if (_team == ETeamID.GR) u.SlotIndex = 1;        // 奇=GR, 与 CalculateTeam 的奇偶规则保持一致
                    u.CurrentBagIndex = 0;

                    // ① 装备 = 角色本体 (把游戏写死的 30001/SWAT 换成我们选的)
                    if (_charItem > 0)
                    {
                        u.Equipment.Init();
                        PlayerUtil.AddEquipment(u.Equipment, _team, _charItem);
                    }

                    // ② 武器袋 (背包1 = 四个武器槽键; 背包2~7 = "背包N" 键, 进图后 B 键切换)
                    bool anyWeapon = false;
                    for (int s = 0; s < 6; s++) if (_item[s] > 0) { anyWeapon = true; break; }
                    bool anyExtra = _bagWid.Count > 0;
                    if (anyWeapon || anyExtra)
                    {
                        // 背包1: 配了武器槽 → 新造; 没配 → 沿用游戏默认那袋 (M4A1/沙鹰/刀)
                        var oldBag0 = (u.WeaponBag != null && u.WeaponBag.Bags.ContainsKey(0)) ? u.WeaponBag.Bags[0] : null;
                        var bag0 = anyWeapon ? BuildBag(_item, _weapon, _raw, null, "我") : oldBag0;
                        if (bag0 == null) bag0 = new CFW.Framework.EventParam_WeaponBag();
                        u.WeaponBag.Bags.Clear();
                        bag0.BagID = 0;
                        u.WeaponBag.Bags.Add(0, bag0);
                        for (int b = 0; b < _bagWid.Count; b++)
                        {
                            var bagX = BuildBagX(_bagItem[b], _bagWid[b], _bagRaw[b], "背包" + (b + 2));
                            bagX.BagID = b + 1;
                            u.WeaponBag.Bags.Add(b + 1, bagX);
                        }
                        u.WeaponBag.CurrentBagIndex = 0;
                    }
                    else if (mine == 0)
                        Debug.LogWarning("[CFZ-Offline][角色武器] 四个武器槽都留空 → 武器保留游戏默认(M4A1/沙鹰/刀), 只改角色");
                    mine++;
                }

                start.MyTeamID = _team;
                start.MySlotIndex = (_team == ETeamID.GR) ? 1 : 0;      // 奇=GR, 偶=BL (与 CalculateTeam 一致)

                if (_applied++ < 3)
                    Debug.LogWarning("[CFZ-Offline][角色武器] 已改写进图数据: 我=" + _char + "(Item " + _charItem + ") 队伍=" + _team +
                                     " 武器槽=" + BagSlotText(start) + " 背包=" + (_bagWid.Count > 0 ? (1 + _bagWid.Count).ToString() + "个" : "1个") +
                                     " | 敌人 " + bots + " 个(换队 " + flip +
                                     ") | 我的条目 " + mine + " 个 | " + EnemyStatus);
                ExportHelp();                                   // ★ 每次进图顺便刷新"可用角色/武器名单"文档

            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][角色武器] 改写进图数据失败: " + e); }
        }

        // ---------------- 敌人 (AI) 改写 ----------------
        //
        //  AI 的 UserInfo 也是 AI_Tutorial_Loading.SetModeInfo 造的 (AI_Tutorial_Loading.cs:116-135):
        //     UserKey=l, SlotIndex=l*2-1 (奇=GR), Team=GR, CurrentCharacterType=SWAT,
        //     bag = M4A1 / DESERTEAGLE / KNIFE, Equipment = AddEquipment(..., GR, 30001)
        //  这里按 [敌人] 段逐项覆盖; **没配的项一个都不碰**(角色沿用 SWAT, 武器沿用 M4A1/沙鹰/刀)。
        //  多个 AI 的分配规则: 第 k 个 AI(从 0 数) 取每个列表的 [k % 列表长度] → 天然"轮流用"
        static void ApplyEnemy(CFW.Framework.EventParam_UserInfo u, int k, ETeamID eTeam)
        {
            try
            {
                // ① 角色 + 装备
                if (_eChar.Count > 0)
                {
                    int ci = k % _eChar.Count;
                    u.CurrentCharacterType = _eChar[ci];
                    u.Equipment.Init();
                    PlayerUtil.AddEquipment(u.Equipment, eTeam, _eCharItem[ci]);
                }

                // ② 武器: 只覆盖配过的槽; 没配的槽从它原有的袋里搬回来 (保持游戏默认, 不会把 AI 的刀弄丢)
                bool any = false;
                for (int s = 0; s < 6; s++) if (_eWid[s].Count > 0) { any = true; break; }
                if (!any) return;

                var item = new int[6]; var wid = new int[6]; var raw = new string[6];
                for (int s = 0; s < 6; s++)
                {
                    if (_eWid[s].Count == 0) continue;
                    int vi = k % _eWid[s].Count;
                    wid[s] = _eWid[s][vi]; item[s] = _eItem[s][vi]; raw[s] = _eRaw[s][vi];
                }
                var old = (u.WeaponBag != null && u.WeaponBag.Bags.ContainsKey(0)) ? u.WeaponBag.Bags[0] : null;
                var bag = BuildBag(item, wid, raw, old, "敌人#" + k);

                u.WeaponBag.Bags.Clear();
                u.WeaponBag.Bags.Add(0, bag);
                u.WeaponBag.CurrentBagIndex = 0;
                u.CurrentBagIndex = 0;
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][敌人] 改写失败: " + e.Message); }
        }

        // 造武器袋: item/wid 里 >0 的槽加进去;
        //   keepFrom 非 null 时, 先把那个袋里已有的槽搬过来 (用于"未配置的槽保持原样")
        static CFW.Framework.EventParam_WeaponBag BuildBag(int[] item, int[] wid, string[] raw,
                                                            CFW.Framework.EventParam_WeaponBag keepFrom, string who)
        {
            var bag = new CFW.Framework.EventParam_WeaponBag();
            bag.BagID = 0;
            bag.CurrentSlot = WEAPONSLOT.WEAPONSLOT_FIRST;
            if (keepFrom != null)
                foreach (var kvp in keepFrom.Slots)
                {
                    if (kvp.Value == null) continue;
                    PlayerUtil.AddWeaponToBag(ref bag, kvp.Value.ItemID, kvp.Value.WeaponID, -1);
                }
            for (int s = 0; s < item.Length; s++)
            {
                if (item[s] <= 0) continue;
                // 提醒: 武器进哪个槽由 WeaponTable 决定, 和 ini 里写的位置无关 ——
                // 比如把步枪写进"武器_副"会占用 主武器 槽(并顶掉原本的主武器)
                WEAPONSLOT nat = SlotOf(wid[s]);
                if (nat != _slotWant[s])
                    Debug.LogWarning("[CFZ-Offline][角色武器] " + who + ": '" + raw[s] + "' 的天然槽位是 " + nat +
                                     ", 不是 " + _slotWant[s] + " → 会占那个槽");
                int before = bag.Slots.Count;
                PlayerUtil.AddWeaponToBag(ref bag, item[s], wid[s], -1);   // -1 = 用 WeaponTable 里的天然槽位
                if (bag.Slots.Count == before)
                    Debug.LogWarning("[CFZ-Offline][角色武器] " + who + ": 武器 '" + raw[s] + "' (weaponID=" + wid[s] +
                                     ") 没进包: WeaponTable 里没有它的弹药条目, AddWeaponToBag 会静默跳过");
            }
            // 手里的枪 = 槽位最小的那把 (游戏自己也这么定: NetClient._CopyBagInfo:1883)
            int minSlot = 99;
            foreach (var kvp in bag.Slots) { int sv = (int)kvp.Key; if (sv < minSlot) minSlot = sv; }
            if (minSlot != 99) bag.CurrentSlot = (WEAPONSLOT)minSlot;
            return bag;
        }

        // 造备用背包 (背包2~7): 与 BuildBag 同一套天然槽位逻辑, 但不做槽位预期检查
        //   (备用袋没有"主/副/近战/投掷"的固定键位预期, 武器进哪个槽完全由 WeaponTable 决定)
        static CFW.Framework.EventParam_WeaponBag BuildBagX(int[] item, int[] wid, string[] raw, string who)
        {
            var bag = new CFW.Framework.EventParam_WeaponBag();
            bag.CurrentSlot = WEAPONSLOT.WEAPONSLOT_FIRST;
            for (int s = 0; s < wid.Length; s++)
            {
                if (item[s] <= 0) continue;
                int before = bag.Slots.Count;
                PlayerUtil.AddWeaponToBag(ref bag, item[s], wid[s], -1);
                if (bag.Slots.Count == before)
                    Debug.LogWarning("[CFZ-Offline][角色武器] " + who + ": 武器 '" + raw[s] +
                                     "' 没进包 (WeaponTable 里没有它的弹药条目)");
            }
            int minSlot = 99;
            foreach (var kvp in bag.Slots) { int sv = (int)kvp.Key; if (sv < minSlot) minSlot = sv; }
            if (minSlot != 99) bag.CurrentSlot = (WEAPONSLOT)minSlot;
            return bag;
        }

        static string BagSlotText(CFW.Framework.EventParam_LoadingStart start)
        {
            try
            {
                for (int i = 0; i < start.UserInfo.Count; i++)
                {
                    var u = start.UserInfo[i];
                    if (u == null || !u.IsMyPlayer) continue;
                    CFW.Framework.EventParam_WeaponBag bag = null;
                    if (u.WeaponBag != null && u.WeaponBag.Bags.ContainsKey(0)) bag = u.WeaponBag.Bags[0];
                    if (bag == null) return "★无袋★";
                    var sb = new System.Text.StringBuilder();
                    foreach (var kvp in bag.Slots)
                        sb.Append((int)kvp.Key).Append(":wid").Append(kvp.Value != null ? kvp.Value.WeaponID : 0).Append(' ');
                    return sb.Length > 0 ? sb.ToString() : "★空袋★";
                }
            }
            catch { }
            return "?";
        }

        // ---------------- 名字解析 ----------------
        static bool TryCharacter(string s, out CharacterType ct)
        {
            ct = CharacterType.SWAT;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            string flat = t.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

            // 1) 枚举名 (SWAT / FOXHOWL / BLACK_RAVEN / ...)
            foreach (object o in Enum.GetValues(typeof(CharacterType)))
            {
                CharacterType v = (CharacterType)o;
                int iv = (int)v;
                if (iv <= 0 || iv >= 99) continue;                        // 跳过 UNKNOWN / NanoType
                string nm = v.ToString();
                if (string.Equals(nm, t, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nm.Replace("_", "").ToLowerInvariant(), flat, StringComparison.OrdinalIgnoreCase))
                { ct = v; return true; }
            }
            // 2) 显示名 (游戏文本表)
            foreach (object o in Enum.GetValues(typeof(CharacterType)))
            {
                CharacterType v = (CharacterType)o;
                int iv = (int)v;
                if (iv <= 0 || iv >= 99) continue;
                string dn = null;
                try { dn = Common.UI.Globalization.TextManager.GetCharacterNameString(iv); } catch { }
                if (string.IsNullOrEmpty(dn)) continue;
                string df = dn.Trim().Replace(" ", "").ToLowerInvariant();
                if (df == flat || df.IndexOf(flat, StringComparison.OrdinalIgnoreCase) >= 0) { ct = v; return true; }
            }
            // 3) 直接写数字
            int n;
            if (int.TryParse(t, out n) && n > 0 && n < 99) { ct = (CharacterType)n; return true; }
            return false;
        }

        // "AK-47, M4A1; MP5 | M60" → {AK-47, M4A1, MP5, M60};  空 → 空列表
        static System.Collections.Generic.List<string> SplitList(string s)
        {
            var res = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(s)) return res;
            foreach (string part in s.Split(',', ';', '|'))
            {
                string one = part.Trim();
                if (one.Length > 0) res.Add(one);
            }
            return res;
        }

        static bool TryWeapon(string s, WEAPONSLOT want, out int weaponId, out int itemId)
        {
            weaponId = 0; itemId = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            foreach (string part in t.Split(',', ';', '|'))              // 一个槽可以写多个候选, 用第一个能用的
            {
                string one = part.Trim();
                if (one.Length == 0) continue;
                if (ResolveOneWeapon(one, want, out weaponId, out itemId)) return true;
            }
            weaponId = 0; itemId = 0;
            return false;
        }

        static bool ResolveOneWeapon(string t, WEAPONSLOT want, out int weaponId, out int itemId)
        {
            weaponId = 0; itemId = 0;
            var it = CFW.DataTable.DataTableManager.ItemTable;
            if (it == null) return false;

            // 1) 直接写 weaponID 数字 (要能在 WeaponTable 里查到槽位才算数)
            int n;
            if (int.TryParse(t, out n) && n > 1000 && SlotOf(n) != WEAPONSLOT.WEAPONSLOT_NONE)
            { weaponId = n; itemId = ItemIndexOf(n); return weaponId != 0; }

            // 2) DefinedWeaponID 常量名: WEAPON_ID_M4A1 / M4A1
            try
            {
                string key = t.ToUpperInvariant().Replace(" ", "").Replace("-", "_");
                if (key.StartsWith("WEAPON_ID_")) key = key.Substring(10);
                foreach (var f in typeof(DefinedWeaponID).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(f.Name, "WEAPON_ID_" + key, StringComparison.OrdinalIgnoreCase)) continue;
                    int id = System.Convert.ToInt32(f.GetValue(null));
                    if (SlotOf(id) != WEAPONSLOT.WEAPONSLOT_NONE) { weaponId = id; itemId = ItemIndexOf(id); return weaponId != 0; }
                }
            }
            catch { }

            // 3) 走武器表显示名 (精确 → 包含; 多个命中时优先"槽位一致 + 名字最短"的那把)
            try
            {
                var all = it.GetAllWeaponID();
                if (all == null || all.IDs == null) return false;
                string flat = t.Replace(" ", "").Replace("-", "").ToLowerInvariant();
                int exact = 0, exactSlotOk = 0;
                int part = 0, partSlotOk = 0; string partName = null; int partNameLen = 99999;
                for (int i = 0; i < all.IDs.Count; i++)
                {
                    int id = all.IDs[i];
                    string nm;
                    if (all.NamesByID == null || !all.NamesByID.TryGetValue(id, out nm) || string.IsNullOrEmpty(nm)) continue;
                    string nf = nm.Replace(" ", "").Replace("-", "").ToLowerInvariant();
                    bool slotOk = (SlotOf(id) == want);
                    if (nf == flat) { if (exact == 0 || (slotOk && exactSlotOk == 0)) { exact = id; exactSlotOk = slotOk ? 1 : 0; } }
                    else if (nf.IndexOf(flat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (part == 0 || (slotOk && partSlotOk == 0) || (slotOk == (partSlotOk == 1) && nm.Length < partNameLen))
                        { part = id; partSlotOk = slotOk ? 1 : 0; partName = nm; partNameLen = nm.Length; }
                    }
                }
                int pick = (exact != 0 && exactSlotOk == 1) ? exact : (exact != 0 ? exact : part);
                if (pick != 0) { weaponId = pick; itemId = ItemIndexOf(pick); return weaponId != 0; }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][角色武器] 查武器表异常: " + e.Message); }
            return false;
        }

        static int ItemIndexOf(int weaponId)
        {
            try { return CFW.DataTable.DataTableManager.ItemTable.GetItemTableIndex(
                        CFW.DataTable.Header.DataTableItemType.Weapon, weaponId); }
            catch { return 0; }
        }

        static WEAPONSLOT SlotOf(int weaponId)
        {
            try { return CFW.DataTable.DataTableManager.WeaponTable.Slot(weaponId); }
            catch { return WEAPONSLOT.WEAPONSLOT_NONE; }
        }

        // ---------------- 名单 (给玩家照着填 ini) ----------------
        public static string CharacterCandidates()
        {
            var sb = new System.Text.StringBuilder();
            foreach (object o in Enum.GetValues(typeof(CharacterType)))
            {
                CharacterType v = (CharacterType)o;
                int iv = (int)v;
                if (iv <= 0 || iv >= 99) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(v.ToString());
                try
                {
                    string dn = Common.UI.Globalization.TextManager.GetCharacterNameString(iv);
                    if (!string.IsNullOrEmpty(dn)) sb.Append('(').Append(dn.Trim()).Append(')');
                }
                catch { }
            }
            return sb.ToString();
        }

        static string WeaponCandidates(WEAPONSLOT want, int max)
        {
            var sb = new System.Text.StringBuilder();
            var seen = new System.Collections.Generic.Dictionary<string, int>();
            try
            {
                var it = CFW.DataTable.DataTableManager.ItemTable;
                if (it == null) return "(数据表未就绪)";
                var all = it.GetAllWeaponID();
                int n = 0;
                for (int i = 0; i < all.IDs.Count && n < max; i++)
                {
                    int id = all.IDs[i];
                    if (SlotOf(id) != want) continue;
                    string nm;
                    if (all.NamesByID == null || !all.NamesByID.TryGetValue(id, out nm) || string.IsNullOrEmpty(nm)) continue;
                    nm = nm.Trim();
                    if (seen.ContainsKey(nm)) continue;                  // 皮肤去重 (同名只列一次)
                    seen[nm] = 1;
                    if (n > 0) sb.Append(", ");
                    sb.Append(nm);
                    n++;
                }
                if (sb.Length == 0) sb.Append("(这个槽位没有可用武器)");
            }
            catch (Exception e) { sb.Append("(枚举失败: " + e.Message + ")"); }
            return sb.ToString();
        }

        public static void Dump()
        {
            try
            {
                if (!Ready) Load();
                Debug.LogWarning("[CFZ-Offline][INI][角色武器] 当前配置: " + Status);
                Debug.LogWarning("[CFZ-Offline][INI][敌人] 当前配置: " + EnemyStatus);
                Debug.LogWarning("[CFZ-Offline][INI][地图] 当前配置: " + MapStatus);
                Debug.LogWarning("[CFZ-Offline][INI][角色武器] 可用角色: " + CharacterCandidates());
                for (int i = 0; i < 4; i++)
                    Debug.LogWarning("[CFZ-Offline][INI][角色武器] " + _slotKey[i] + " 候选(" + _slotWant[i] + "): " +
                                     WeaponCandidates(_slotWant[i], 30));
                ExportHelp();                                   // ★ 顺便把完整名单(不截断)导成文档
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][INI][角色武器] Dump 失败: " + e.Message); }
        }

        // ---------------- ★名单导出 (帮助文档)★ ----------------
        //
        //  为什么由 mod 生成: 数据表是二进制 (DataTables/*.bytes, 里面没有可读字符串),
        //  离线没法解析出"名字 → ID"; 只有运行时 TextManager/ItemTable 才拿得到。
        //  所以名单由 mod 自己导, 好处是和游戏版本永远同步、内容完整(不截断)、每件武器带 weaponID/itemID。
        //  文件放在 ini 同目录:  Helper.md
        //  刷新时机: F10(重载) / 每次进图(F9/按钮)  —— 同一份配置只写一次, 不会反复写盘。

        static int _ver = 0;
        static int _exported = -1;
        static int _helpLog = 0;

        public static string HelpPath()
        {
            try
            {
                string d = System.IO.Path.GetDirectoryName(ModConfig.Path);
                if (string.IsNullOrEmpty(d)) d = ".";
                return System.IO.Path.Combine(d, "Helper.md");
            }
            catch { return "Helper.md"; }
        }

        public static void ExportHelp()
        {
            try
            {
                if (!Ready) Load();
                if (!Ready) return;
                if (_exported == _ver) return;
                var sb = new System.Text.StringBuilder();
                BuildHelp(sb);
                System.IO.File.WriteAllText(HelpPath(), sb.ToString(), new System.Text.UTF8Encoding(true));
                _exported = _ver;
                if (_helpLog++ < 3) Debug.LogWarning("[CFZ-Offline][角色武器] 可用角色/武器名单已写入 → " + HelpPath());
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][角色武器] 名单导出失败: " + e.Message); }
        }

        static void BuildHelp(System.Text.StringBuilder sb)
        {
            string bar = "============================================================";
            sb.AppendLine(bar);
            sb.AppendLine(" CFZ Offline  ── 角色 / 武器 名单 + 用法");
            sb.AppendLine(" (本文件由 mod 在运行时自动生成, 与游戏数据同步; 游戏里按 F10 可立即刷新)");
            sb.AppendLine(bar);
            sb.AppendLine(" 生成时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try { sb.AppendLine(" 游戏版本 : " + Application.version + " / Unity " + Application.unityVersion); } catch { }
            sb.AppendLine(" 数据来源 : DataTableManager.ItemTable.GetAllWeaponID() + Common.UI.Globalization.TextManager");
            sb.AppendLine(" 当前配置 : " + Status);
            sb.AppendLine();

            sb.AppendLine("【一、填在哪里】");
            sb.AppendLine("  和本文件同目录的 Config.ini → [角色武器] 段:");
            sb.AppendLine();
            sb.AppendLine("      [角色武器]");
            sb.AppendLine("      角色      = SWAT");
            sb.AppendLine("      队伍      = BL");
            sb.AppendLine("      武器_主   = AK-47");
            sb.AppendLine("      武器_副   = Desert Eagle");
            sb.AppendLine("      武器_近战 = KA-BAR Serrated Knife");
            sb.AppendLine("      武器_投掷 = M67 Grenade");
            sb.AppendLine();
            sb.AppendLine("  存盘后: 游戏里按 F10 立即生效(不用重启); 按 F9 进图时也会自动读取。");
            sb.AppendLine("  敌人(AI)另有一个 [敌人] 段 —— 见下面【五】。");
            sb.AppendLine("  想恢复游戏默认(固定 SWAT + M4A1/沙鹰/刀): 把整个 [角色武器] 段删掉即可 ——");
            sb.AppendLine("  mod 检测不到这个段就什么都不改。");
            sb.AppendLine();

            sb.AppendLine("【二、名字怎么写】");
            sb.AppendLine("  · 不区分大小写; 空格和短横线可以省  →  写 \"AK47\" 也能对上 \"AK-47\"");
            sb.AppendLine("  · 一个槽可以写多个候选, 用 , ; | 分隔, 取第一个能用的");
            sb.AppendLine("  · 也可以直接写编号, 就是下面每行括号里的 weaponID:  武器_主 = 10101000");
            sb.AppendLine("  · 角色可以写枚举名(SWAT)、显示名(Fox Howl)或数字");
            sb.AppendLine("  · 写错的名字 → 该槽保留游戏默认 + 日志里打警告(带该槽候选名单), 不会崩");
            sb.AppendLine();

            sb.AppendLine("【三、角色】共 " + CharCount() + " 个, 写法: 角色 = <枚举名 或 显示名>");
            AppendCharacters(sb);
            sb.AppendLine("  ★ 这 12 个角色的 in-game 模型包本地都齐 (Bundle/Character/fps_m_*.unity3d 等)");
            sb.AppendLine();

            sb.AppendLine("【四、武器】写法: 武器_主 / 武器_副 / 武器_近战 / 武器_投掷 = <名字>");
            sb.AppendLine("  ★ 武器进哪个槽由游戏的 WeaponTable 决定, 和写在哪个键无关 ——");
            sb.AppendLine("    把步枪写到 武器_副 会占掉主武器槽(并顶掉原主武器); 出现这种情况 mod 会打警告。");
            sb.AppendLine("  ★ 留空 = 该槽不放武器; 四个槽都留空 = 武器保留游戏默认, 只改角色。");
            sb.AppendLine("  ★ 名字取自游戏文本表, 同族武器靠后缀区分:  -Camo / -Silver / -Gold / -Blue Crystal ...");
            sb.AppendLine();
            sb.AppendLine("【四点五、备用背包】写法: [角色武器] 段 背包2 ~ 背包7 = 武器名, 武器名, ...");
            sb.AppendLine("  · 进图后按 B 呼出背包面板, 点数字键切换 (背包1 = 上面四个武器槽键配的那套)");
            sb.AppendLine("  · 逗号分隔, 最多 6 把/袋; 名字写法与上面完全一样; 没写的槽 = 那个背包不带");
            sb.AppendLine("  · 例:  背包2 = AK-47, Desert Eagle, Knife        背包3 = AWM");
            sb.AppendLine("  · 只配 背包2 不配四个槽 → 背包1 自动沿用游戏默认 (M4A1/沙鹰/刀)");
            sb.AppendLine();

            AppendWeaponList(sb, WEAPONSLOT.WEAPONSLOT_FIRST, "主武器", "武器_主");
            AppendWeaponList(sb, WEAPONSLOT.WEAPONSLOT_SECOND, "副武器(手枪)", "武器_副");
            AppendWeaponList(sb, WEAPONSLOT.WEAPONSLOT_THIRD, "近战", "武器_近战");
            AppendWeaponList(sb, WEAPONSLOT.WEAPONSLOT_FOURTH_1, "投掷物", "武器_投掷");

            sb.AppendLine("【五、敌人 / AI 机器人】段名 [敌人] —— 几个敌人、各用什么角色和枪");
            sb.AppendLine("      数量      = 4                      ← 敌人(AI) 数量, 1~7, 游戏默认 4");
            sb.AppendLine("      杀敌数    = 20                     ← 回合胜利需要的击杀数(HUD 的 X/目标), 最小 2, 游戏默认 20");
            sb.AppendLine("      难度      = 普通                    ← 简单 / 普通(游戏原值) / 困难 / 自定义");
            sb.AppendLine("                 简单: 抖动14,4 间隔0.45s 索敌40m 复活6s 速度6");
            sb.AppendLine("                 普通: 抖动7,2  间隔0.2s  索敌80m 复活3s 速度8 (= 游戏原值)");
            sb.AppendLine("                 困难: 抖动0.5,0.5 间隔0.2s 索敌80m 复活3s 速度8 (只收紧瞄准)");
            sb.AppendLine("      瞄准抖动  = 7, 2                     ← 横向, 纵向(米): AI 唯一的命中率, 0 = 百发百中");
            sb.AppendLine("      开火间隔  = 0.2                      ← 秒, 越小打得越密");
            sb.AppendLine("      索敌距离  = 80 / 视野半径 = 6         ← 米: 超出就看不见你");
            sb.AppendLine("      复活时间  = 3 / AI血量 = 100          ← AI 死后多久复活 / 复活时血量");
            sb.AppendLine("      移动速度  = 8                        ← 0 = 站着不动");
            sb.AppendLine("    [地图] 段: 换图功能已移除 —— 恒用游戏默认教学图 Transportship_ren");
            sb.AppendLine("            (ini 里旧的『地图/敌人出生』配置已忽略)");
            sb.AppendLine("      角色      = SAS, FOXHOWL            ← 可以写多个");
            sb.AppendLine("      武器_主   = AK-47, M4A1             ← 第1个AI用AK-47, 第2个用M4A1, 第3个又回AK-47 ...");
            sb.AppendLine("      武器_副   = Desert Eagle");
            sb.AppendLine("      武器_近战 = Kukri");
            sb.AppendLine("      武器_投掷 = M67 Grenade");
            sb.AppendLine();
            sb.AppendLine("  · 数量上限 7: 游戏取名池只有 7 个名字(BotMaxName) 且要求互不重复, 写更大只会被夹到 7");
            sb.AppendLine("  · 数量 >4 时地图刷点可能不够 → mod 自动借用合法刷点, 日志打 [刷点] 提示 (不会崩/不会卡加载)");
            sb.AppendLine("  · 杀敌数最小 2: 0/1 会被游戏当成「按时间结束」的目标(-1) → 一进图回合立刻结算, 所以夹到 2");
            sb.AppendLine("  · 难度是「运行时」生效的(改完 F10 就行), 只有 移动速度 要在进图创建 AI 时才读一次");
            sb.AppendLine("  · 难度对应的原始常量: 抖动 -7/7/-2/2 (AI_TutorialPlayer.cs:187) | 间隔 0.2 (:FireStarting 60)");
            sb.AppendLine("    视野 6/80 (AI_TutorialHitCheck.cs:18/37) | 复活 3 秒 + 100 血 (AI_TutorialPlayerState_die.cs:51/57)");
            sb.AppendLine("    移动速度 8 (NpcPathFinder.cs:19) —— 启动日志会打印每个方法「换掉几处常量」, 0 就是没匹配上");
            sb.AppendLine("  · 写法和上面一样, 区别是每个键都能写**列表**, 按 AI 顺序轮流分配 (第 k 个 AI 取 列表[k % 长度])");
            sb.AppendLine("  · 整段留空 = 一个都不碰, AI 完全沿用游戏默认 (4 个: SWAT + M4A1/沙鹰/刀)");
            sb.AppendLine("  · 只写了某个槽 = 只换那个槽, 其它槽保持游戏默认");
            sb.AppendLine("  · 队伍不用配: AI 永远和你对立 (你 BL → AI GR; 你 GR → AI BL)");
            sb.AppendLine("  · 当前 [敌人] 配置: " + EnemyStatus);
            sb.AppendLine();

            sb.AppendLine("【六、队伍】");
            sb.AppendLine("  队伍 = BL   → 你在 BL, 3 个 AI 在 GR        (游戏默认)");
            sb.AppendLine("  队伍 = GR   → 你在 GR, AI 会被自动换到 BL, 保证还有对手可打");
            sb.AppendLine();

            sb.AppendLine("【七、改完怎么确认】");
            sb.AppendLine("  游戏里按 F10  →  本文件刷新 + 日志打印解析结果, 形如:");
            sb.AppendLine("     [CFZ-Offline][INI][角色武器] 配置已解析: 角色=SWAT(Item 30001) 队伍=BL 武器=4/4 \\");
            sb.AppendLine("        [武器_主=AK-47(weaponID 10101000/item 10001) | 武器_副=Desert Eagle(...) | ...]");
            sb.AppendLine("  括号里就是真正装进枪袋的 weaponID / itemID —— 和上面名单一致就说明写对了。");
            sb.AppendLine();
            sb.AppendLine("【八、例: 换角色 + 换整套枪 (复制进 ini 即可)】");
            sb.AppendLine("      角色      = FOXHOWL");
            sb.AppendLine("      队伍      = GR");
            sb.AppendLine("      武器_主   = M4A1-Silencer");
            sb.AppendLine("      武器_副   = Desert Eagle-Gold");
            sb.AppendLine("      武器_近战 = Kukri");
            sb.AppendLine("      武器_投掷 = M18 Smoke Grenade");
            sb.AppendLine();
            sb.AppendLine("【九、顺带说明: 这套配置是怎么生效的】");
            sb.AppendLine("  离线没有服务器, 进图时「我是谁、拿什么枪」这份数据是游戏自己硬编码造出来的:");
            sb.AppendLine("     AI_Tutorial_Loading.cs:39   CurrentCharacterType = CharacterType.SWAT    ← 你的角色(写死)");
            sb.AppendLine("     AI_Tutorial_Loading.cs:69   i=0 M4A1 / i=1 DESERTEAGLE / i=2 KNIFE       ← 你的武器(写死)");
            sb.AppendLine("     AI_Tutorial_Loading.cs:82   PlayerUtil.AddEquipment(Equipment, BL, 30001) ← 装备(30001=SWAT)");
            sb.AppendLine("     AI_Tutorial_Loading.cs:116-135  4 个 AI: SWAT + M4A1/沙鹰/刀, 队伍 GR      ← 敌人(写死)");
            sb.AppendLine("  然后 LoadingUI._Loading 协程把它变成真正的玩家 (中间隔着整张地图的异步加载)。");
            sb.AppendLine("  mod 就在 SetModeInfo 返回之后、建玩家之前, 把 NetEvent.Param_LoadingStart.UserInfo");
            sb.AppendLine("  里每一项整个重写 —— 所以你(第 0 项)和敌人(第 1..4 项)一起生效, 改完不用重启。");
            sb.AppendLine();
            sb.AppendLine(bar);
            sb.AppendLine(" 名单结束 (完整, 未截断)");
            sb.AppendLine(bar);
        }

        static int CharCount()
        {
            int n = 0;
            foreach (object o in Enum.GetValues(typeof(CharacterType)))
            {
                int iv = (int)(CharacterType)o;
                if (iv > 0 && iv < 99) n++;
            }
            return n;
        }

        static void AppendCharacters(System.Text.StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("      枚举名      显示名            装备Item   角色枚举值");
            sb.AppendLine("      ----------  ----------------  ---------  ----------");
            foreach (object o in Enum.GetValues(typeof(CharacterType)))
            {
                CharacterType v = (CharacterType)o;
                int iv = (int)v;
                if (iv <= 0 || iv >= 99) continue;                    // 跳过 UNKNOWN / NanoType
                string dn = "";
                try { dn = Common.UI.Globalization.TextManager.GetCharacterNameString(iv); } catch { }
                if (dn == null) dn = "";
                int item = 0;
                try
                {
                    item = CFW.DataTable.DataTableManager.ItemTable.GetItemTableIndex(
                               CFW.DataTable.Header.DataTableItemType.Character, iv);
                }
                catch { }
                sb.Append("      ").Append(Pad(v.ToString(), 12)).Append(Pad(dn.Trim(), 18))
                  .Append(Pad(item > 0 ? item.ToString() : "-", 11)).Append(iv).AppendLine();
            }
        }

        static void AppendWeaponList(System.Text.StringBuilder sb, WEAPONSLOT want, string title, string key)
        {
            sb.AppendLine("  ── " + title + "  (" + key + " = <名字>)  ──");
            try
            {
                var it = CFW.DataTable.DataTableManager.ItemTable;
                if (it == null) { sb.AppendLine("       (数据表未就绪)"); sb.AppendLine(); return; }
                var all = it.GetAllWeaponID();
                var seen = new System.Collections.Generic.Dictionary<string, int>();
                int n = 0;
                for (int i = 0; i < all.IDs.Count; i++)
                {
                    int id = all.IDs[i];
                    if (SlotOf(id) != want) continue;
                    string nm;
                    if (all.NamesByID == null || !all.NamesByID.TryGetValue(id, out nm) || string.IsNullOrEmpty(nm)) continue;
                    nm = nm.Trim();
                    if (seen.ContainsKey(nm)) continue;               // 同名皮肤去重, 只列第一个
                    seen[nm] = 1;
                    sb.Append("      ").Append(Pad(nm, 34)).Append("weaponID ").Append(Pad(id.ToString(), 13))
                      .Append("item ").AppendLine(ItemIndexOf(id).ToString());
                    n++;
                }
                if (n == 0) sb.AppendLine("      (这个槽位没有可用武器)");
                else sb.AppendLine("      共 " + n + " 个");
            }
            catch (Exception e) { sb.AppendLine("      (枚举失败: " + e.Message + ")"); }
            sb.AppendLine();
        }

        static string Pad(string s, int w)
        {
            if (s == null) s = "";
            // 中文按 2 个字符宽算, 让名单在记事本里也能对齐
            int width = 0;
            for (int i = 0; i < s.Length; i++) width += (s[i] > 0x2E80) ? 2 : 1;
            if (width >= w) return s + "  ";
            return s + new string(' ', w - width + 2);
        }
    }

    public class CfzOfflineDriver : MonoBehaviour
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static;

        CFW.UI.Lobby.LobbyClient lc;
        CFW.Network.LobbyNetClient lnc;
        UnityEngine.Object waited;

        void Update()
        {
            try
            {
#if DEBUG
                // ---- 调试快捷键 (仅 Debug 版): F1~F8/F11/F12/Shift+F10 是排查工具, Release 版全部编译剔除 ----
                if (Input.GetKeyDown(KeyCode.F1)) { Debug.Log("[CFZ-Offline] F1 -> 探测地图 bundle 加载"); ProbeMapBundle(); }
                else if (Input.GetKeyDown(KeyCode.F2)) { Debug.Log("[CFZ-Offline] F2 -> 强制 isGameSceneLoadingComplete=true"); ForceGameSceneLoadingComplete(); }
                else if (Input.GetKeyDown(KeyCode.F3)) { Debug.Log("[CFZ-Offline] F3 -> 强制发 GameMode_Init"); ForceGameModeInit(); }
                else if (Input.GetKeyDown(KeyCode.F4)) { Debug.Log("[CFZ-Offline] F4 -> 重发 InGameUI_OnLoad"); ResendInGameUIOnLoad(); }
                else if (Input.GetKeyDown(KeyCode.F5)) { Debug.Log("[CFZ-Offline] F5 -> 全面诊断"); LogDiag(); Loadout.Dump(); }
                else if (Input.GetKeyDown(KeyCode.F6)) { Debug.Log("[CFZ-Offline] F6 -> 强制关闭 Loading 界面"); ForceEndLoading(); }
                else if (Input.GetKeyDown(KeyCode.F7)) { Debug.Log("[CFZ-Offline] F7 -> InGame (真实地图, 无 AI)"); EnterInGame(); }
                else if (Input.GetKeyDown(KeyCode.F8)) { Debug.Log("[CFZ-Offline] F8 -> Tutorial (单机教学场景)"); EnterTutorial(); }
                else
#endif
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    Debug.Log("[CFZ-Offline] F9 -> Tutorial_AI (InGame 地图 + AI 机器人)");
                    EnterAICombat();
                }
                else if (Input.GetKeyDown(KeyCode.F10))
                {
#if DEBUG
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    {
                        Debug.Log("[CFZ-Offline] Shift+F10 -> 列出构建场景"); LogScenes();
                    }
                    else
#endif
                    {
                        Debug.Log("[CFZ-Offline] F10 -> 重新加载 ini (键位表 + 角色武器)");
                        ModConfig.EnsureAndLoad();
                        Loadout.Load();
#if DEBUG
                        Loadout.Dump();
#endif
                    }
                }
#if DEBUG
                else if (Input.GetKeyDown(KeyCode.F11)) { Debug.Log("[CFZ-Offline] F11 -> 修复 UISet 并重跑 Loading"); RepairAndRestartLoading(); }
                else if (Input.GetKeyDown(KeyCode.F12)) { Debug.Log("[CFZ-Offline] F12 -> 重新生成并热加载 bundle 清单"); RegenerateBundleList(); ReloadBundleListLive(); }
#endif
            }
            catch { }
            PlayerWatchdog();
            AutoFixInGameUI();
        }

        // ==================== 玩家生成链路追踪 ====================

        static float _lastWatch = -100f;

        static object Refl(object o, string field)
        {
            if (o == null) return null;
            try
            {
                var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return f != null ? f.GetValue(o) : null;
            }
            catch { return null; }
        }

        public static bool CreatePlayerPrefix(CFW.InGame.Player.PlayerManager __instance, object eventParam)
        {
            try
            {
                object uk = Refl(eventParam, "UserKey");
                var dic = Refl(__instance, "dicLoadingPlayers") as System.Collections.IDictionary;
                var dp = Refl(__instance, "dicPlayers") as System.Collections.IDictionary;
                bool inDic = dic != null && uk != null && dic.Contains(uk);
                bool inPlayers = dp != null && uk != null && dp.Contains(uk);
                Debug.Log("[CFZ-Offline] ◆◆ CreatePlayer UserKey=" + uk +
                          " Slot=" + Refl(eventParam, "SlotIndex") + " IsMy=" + Refl(eventParam, "IsMyPlayer") +
                          " Nick='" + Refl(eventParam, "NickName") + "' 已在加载字典=" + inDic + " 已是玩家=" + inPlayers);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] CreatePlayerPrefix 异常: " + e.Message); }
            return true;
        }

        public static bool CreatePlayerCoroutinePrefix(object eventParam)
        {
            try
            {
                bool sc = true;
                try { sc = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera != null; } catch { }
                Debug.Log("[CFZ-Offline] ◆◆ _CreatePlayer 协程启动 UserKey=" + Refl(eventParam, "UserKey") +
                          " | InGameUI.IsLoadComplete=" + CFW.UI.InGameUI.InGameUI.IsLoadComplete +
                          " SceneCamera!=null=" + sc + " ModeType=" + CFW.InGame.InGame.ModeType);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] CreatePlayerCoroutinePrefix 异常: " + e.Message); }
            return true;
        }

        public static bool PlayerOnCreatePrefix(object eventParam)
        {
            try { Debug.Log("[CFZ-Offline] ◆◆ Player.OnCreate UserKey=" + Refl(eventParam, "UserKey") + " Nick='" + Refl(eventParam, "NickName") + "'"); }
            catch { }
            return true;
        }

        public static bool PlayerManagerAddPlayerPrefix(object team, object player)
        {
            try { Debug.Log("[CFZ-Offline] ◆◆ PlayerManager.AddPlayer team=" + team + " player!=null=" + (player != null)); }
            catch { }
            return true;
        }

        // ==================== 通用反射工具 (兼容 字段/属性) ====================

        const BindingFlags F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static object Member(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var f = t.GetField(name, F2);
                if (f != null) return f.GetValue(o);
                var pr = t.GetProperty(name, F2);
                if (pr != null && pr.CanRead) return pr.GetValue(o, null);
            }
            catch { }
            return null;
        }

        static object StaticMember(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var f = t.GetField(name, SF);
                if (f != null) return f.GetValue(null);
                var p = t.GetProperty(name, SF);
                if (p != null && p.CanRead) return p.GetValue(null, null);
            }
            catch { }
            return null;
        }

        // ==================== 音频 / 弹孔 探针 ====================

        static int _audioEvt = 0;
        static int _fxReq = 0;
        static int _fxIn = 0;
        static int _hitFx = 0;

        // AudioEventHandler.OnPlaySound(GameEvent, IGameEventParam) 的 prefix
        // ★用 prefix 而非 postfix: 若内部 (AudioManager.Listener==null) 抛异常, postfix 根本不会执行
        public static void OnPlaySoundPrefix(object __0, object __1)
        {
            try
            {
                _audioEvt++;
                if (_audioEvt <= 12 || _audioEvt % 100 == 0)
                    Debug.LogWarning("[CFZ-Offline][声音] PlayAudio 到达 #" + _audioEvt +
                                     " name=" + Member(__1, "SoundName") +
                                     " type=" + Member(__1, "Type") +
                                     " 事件音量=" + Member(__1, "Volume"));
            }
            catch { }
        }

        // FXManager._Play(EventParam_PlayFX, ObjectPoolEntity) 的 postfix
        public static void FXPlayPostfix(object __0, object __1)
        {
            try
            {
                _fxReq++;
                if (_fxReq <= 12 || _fxReq % 100 == 0)
                    Debug.LogWarning("[CFZ-Offline][弹孔/特效] FX 请求 #" + _fxReq +
                                     " name=" + Member(__0, "FXName") +
                                     " 池对象=" + (__1 != null ? "有" : "★null★(Prefabs/Effects 没加载出来)"));
                if (__1 != null) DumpFxObject(__1);      // ★ 池对象非 null ≠ 看得见, 前 3 个把材质/层号/相机遮罩全打出来
            }
            catch { }
        }

        // ★FX 到底"看得见"没有★
        //   池对象非 null 只证明预制体取到了; 三种"取了但看不见"的死法:
        //     ① 预制体材质的 Shader 没打进包 → 粉红块或完全不可见 (Effect/*.unity3d 是独立包, 依赖最容易断)
        //     ② 物体/子 Renderer 的层号不在任何相机 cullingMask 里 (主相机 mask=-1025, FPVCamera=256)
        //     ③ Renderer/粒子没启用
        static int _fxDump = 0;

        public static void DumpFxObject(object entity)
        {
            try
            {
                if (_fxDump >= 3) return;
                var comp = entity as UnityEngine.Component;
                if (comp == null) return;
                _fxDump++;
                var go = comp.gameObject;
                var ps = go.GetComponentsInChildren<ParticleSystem>(true);
                var rs = go.GetComponentsInChildren<Renderer>(true);
                var cams = Camera.allCameras;

                var sb = new System.Text.StringBuilder();
                sb.Append("[CFZ-Offline][FX渲染] '").Append(go.name).Append("' activeInHierarchy=")
                  .Append(go.activeInHierarchy).Append(" layer=").Append(go.layer)
                  .Append(" 粒子=").Append(ps.Length).Append(" Renderer=").Append(rs.Length);

                int playing = 0;
                for (int i = 0; i < ps.Length; i++) if (ps[i].isPlaying) playing++;
                sb.Append(" isPlaying=").Append(playing);

                for (int i = 0; i < rs.Length && i < 3; i++)
                {
                    var r = rs[i];
                    int seenBy = 0;
                    for (int c = 0; c < cams.Length; c++)
                        if (cams[c] != null && (cams[c].cullingMask & (1 << r.gameObject.layer)) != 0) seenBy++;
                    var m = r.sharedMaterial;
                    sb.Append(" | r").Append(i).Append("(L=").Append(r.gameObject.layer)
                      .Append(" 可见相机=").Append(seenBy).Append('/').Append(cams.Length)
                      .Append(" enabled=").Append(r.enabled)
                      .Append(" mat=").Append(m != null ? m.name : "★null★");
                    if (m != null && m.shader != null)
                        sb.Append(" shader=").Append(m.shader.name).Append(" 支持=").Append(m.shader.isSupported);
                    sb.Append(")");
                }
                Debug.LogWarning(sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][FX渲染] dump 异常: " + e.Message); }
        }

        // FXManager._Play 的 prefix —— 与 postfix 配对
        //   ★为什么两个都要★ postfix 在方法内部抛异常时不会执行; 只有 prefix 能证明"到底有没有进 _Play"
        public static void FXPlayPrefix(object __0, object __1)
        {
            try
            {
                _fxIn++;
                if (_fxIn <= 12 || _fxIn % 50 == 0)
                    Debug.LogWarning("[CFZ-Offline][弹孔/特效] _Play 进入 #" + _fxIn +
                                     " name=" + Member(__0, "FXName") +
                                     " 池对象=" + (__1 != null ? "有" : "★null★(预制体没取到)"));
            }
            catch { }
        }

        // FXManager.Play(IGameEventParam, ObjectPoolEntity) —— ObjectPoolManager 加载完预制体后的回调
        //   被调用 = 预制体**从 bundle 取到了**; 不被调用 = _MakePool 里 prefab==null 静默放弃
        //   (ObjectPoolManager.cs:79-88) —— 这是"FX 到底死在哪一步"的分水岭
        static int _fxCb = 0;
        public static void FXManagerPlayPrefix(object __0, object __1)
        {
            try
            {
                _fxCb++;
                if (_fxCb <= 12 || _fxCb % 50 == 0)
                    Debug.LogWarning("[CFZ-Offline][弹孔/特效] 池对象回调 Play #" + _fxCb +
                                     " name=" + Member(__0, "FXName") +
                                     " 实体=" + (__1 != null ? "有" : "★null★"));
            }
            catch { }
        }

        // BundleLoadJob.MakeComplete 的 postfix —— 每个 bundle 加载任务的最终结果
        //   用来判定"闸门放开后, 资源到底有没有从 bundle 里取出来"
        static int _jobLog = 0;
        static int _jobNull = 0;
        static int _jobCh = 0;
        public static void BundleJobCompletePostfix(object __instance)
        {
            try
            {
                string an = Member(__instance, "AssetName") as string;
                string bn = Member(__instance, "BundleName") as string;
                object asset = Member(__instance, "LoadedAsset");

                // ★ 场景包 (BundleType=Scene) 里的 "资产" 是 .unity 场景, 用 Load<GameObject> 取必然 null,
                //   这是设计如此不是故障 (例: transportship_ren → ResourcePath=["Assets/LevelResources/.../transportship_ren.unity"])
                //   → 从"失败"统计里排除, 免得掩盖真问题
                object info = Member(__instance, "BundleInfo");
                object bt = info != null ? Member(info, "BundleType") : null;
                bool isScene = bt != null && bt.ToString() == "Scene";

                bool fx = an != null && (an.IndexOf("Effects", StringComparison.OrdinalIgnoreCase) >= 0
                                      || an.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                                      || an.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));
                bool ch = an != null && an.IndexOf("CHARACTER", StringComparison.OrdinalIgnoreCase) >= 0;

                if ((fx && _jobLog++ < 20) || (ch && _jobCh++ < 10) || (asset == null && !isScene && _jobNull++ < 12))
                    Debug.LogWarning("[CFZ-Offline][bundle] 加载结束 bundle='" + bn + "' asset='" + an + "' → " +
                                     (asset != null ? "★OK★" : (isScene ? "null(场景包,正常)" : "★null★ (文件缺失/资源名对不上)")));
            }
            catch { }
        }

        // Player.MakeHitEffectCommon(...) 的 prefix —— object[] __args 兼容任意签名
        public static void MakeHitEffectPrefix(object __instance, object[] __args)
        {
            try
            {
                _hitFx++;
                if (_hitFx <= 12)
                {
                    string a = "";
                    if (__args != null)
                        for (int i = 0; i < __args.Length && i < 4; i++) a += "[" + i + "]=" + __args[i] + " ";
                    Debug.LogWarning("[CFZ-Offline][弹孔] MakeHitEffectCommon 被调用 #" + _hitFx + " args=" + a);

                    // ★★★ 弹孔/FX 的真正判定点就在这里 (Player.cs:2073-2091) ★★★
                    //   material = DataTableManager.MaterialTable.GetMaterial(textureID)
                    //   if (material.HasValue) {
                    //       if (material.Value.BulletMarkFxLength > 0) MakeParticleFX(BulletMark, BulletMarkFx(rand), ...)
                    //       MakeParticleFX(Impact, material.Value.ImpactFx, ...)      ← ★ fxName 为空则什么都不发
                    //       PlaySoundEffect(EffectHit, material.Value.HitSoundName)   ← 声音正常 → 前面必然都执行到了
                    //   }
                    //   而 FXPlayer.MakeParticleFX 第一行是 if (!string.IsNullOrEmpty(fxName))
                    //   → 材质表这两列是空 = 一声不响地跳过 (与"声音调得通、弹孔没有"完全吻合)
                    try
                    {
                        if (__args != null && __args.Length > 0 && __args[0] != null)
                        {
                            object mid = __args[0];
                            // ★走反射: MaterialEntry 实现了 FlatBuffers.IFlatbufferObject,
                            //   直接写 GetMaterial(...) 会让编译器去要 FlatBuffers 程序集引用
                            object mt = StaticMember(typeof(CFW.DataTable.DataTableManager), "MaterialTable");
                            object mv = null;
                            if (mt == null) Debug.LogWarning("[CFZ-Offline][弹孔] ★DataTableManager.MaterialTable 为 null★");
                            else
                            {
                                var gm = mt.GetType().GetMethod("GetMaterial",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new Type[] { typeof(CFW.Physics.MaterialType) }, null);
                                if (gm == null) Debug.LogWarning("[CFZ-Offline][弹孔] ★MaterialTable.GetMaterial 没找到★");
                                else mv = gm.Invoke(mt, new object[] { mid });
                            }
                            if (mt != null && mv == null)
                            {
                                Debug.LogWarning("[CFZ-Offline][弹孔] ★GetMaterial(" + mid + ") 返回 null★ → 弹孔/冲击FX 全被跳过");
                            }
                            else
                            {
                                Debug.LogWarning("[CFZ-Offline][弹孔] 材质表 " + mid +
                                    " | ImpactFx='" + Member(mv, "ImpactFx") + "'" +
                                    " BulletMarkFxLength=" + Member(mv, "BulletMarkFxLength") +
                                    " HitSoundName='" + Member(mv, "HitSoundName") + "'");
                            }
                        }
                    }
                    catch (Exception e3) { Debug.LogWarning("[CFZ-Offline][弹孔] 读材质表异常: " + e3.Message); }
                }
            }
            catch { }
        }

        // AudioManager.PlaySound(string name, Transform parent, Vector3 position, float volume,
        //                        int index, bool ignoreListenerVolume, bool loop) 的 prefix
        static int _sndPlay = 0;
        public static void AudioPlaySoundPrefix(object __0, object __3)
        {
            try
            {
                _sndPlay++;
                if (_sndPlay <= 12 || _sndPlay % 100 == 0)
                    Debug.LogWarning("[CFZ-Offline][声音] AudioManager.PlaySound #" + _sndPlay +
                                     " name=" + __0 + " 音量=" + __3);
                if (_sndPlay <= 3) DumpAudioState(__0 as string);       // ★ 前 3 次把声音链路三段全打出来
                if (_sndPlay == 4) DumpFxState();                       // ★ 打一次 FX/后处理系统状态
            }
            catch { }
        }

        // ==================== ★声音链路诊断★ 一次把三段全打出来 ====================
        //
        //  AudioEventHandler.OnPlaySound  →  AudioManager.PlaySound(key, ...)      ← 探针已证明这里通了(音量≈1)
        //      ├─① _soundList 里有 key 且 IsValid() → _PlayAudioInfo 直接播
        //      │      └─ _PlayAudioInfo 第一行 Vector3.Distance(Listener.transform...)  ← Listener=null 会 NRE
        //      └─② 没有 → StartCoroutine(LoadAudio(key, cb))                       ← ★ 静音且零报错的元凶在这
        //             LoadAudio: GetSoundKeyData(key)==null → **yield break**
        //                        info.Loaded==false        → **不调 onLoad**
        //             AudioInfo.LoadClip: ResourceLoader.Load<AudioClip>(entry.Bundle, "...wav")
        //                                 → _clips[i]==null 则 Loaded=false      ← 与模型同一套 bundle 分流

        static void DumpAudioState(string key)
        {
            try
            {
                // ① 数据表里有这个 key 吗
                //    ★走反射: SoundKeyEntry 同样是 FlatBuffers 生成的, 直接引用会牵出 FlatBuffers 程序集
                string tbl;
                try
                {
                    object st = StaticMember(typeof(CFW.DataTable.DataTableManager), "SoundTable");
                    if (st == null) tbl = "★DataTableManager.SoundTable 为 null★ → 声音表根本没加载";
                    else
                    {
                        var gm = st.GetType().GetMethod("GetSoundKeyData",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new Type[] { typeof(string) }, null);
                        if (gm == null) tbl = "★SoundTable.GetSoundKeyData 方法没找到★";
                        else
                        {
                            object kd = gm.Invoke(st, new object[] { key });
                            if (kd == null)
                            {
                                tbl = "★SoundTable 里没有 '" + key + "'★ → LoadAudio 直接 yield break (静音, 零报错)";
                            }
                            else
                            {
                                object sv = Member(kd, "Value");
                                tbl = "Key=" + Member(sv, "Key") + " Group=" + Member(sv, "Group") +
                                      " Bundle='" + Member(sv, "Bundle") + "'" +
                                      " 文件数=" + Member(sv, "FileNameLength");
                            }
                        }
                    }
                }
                catch (Exception e1) { tbl = "★查询异常(" + e1.GetType().Name + "): " + e1.Message + "★"; }

                // ② 缓存里有没有它 / clip 到位没
                string cl = "?";
                var am = CFW.GameSystem.Audio.AudioManager.Instance;
                var list = Member(am, "_soundList") as System.Collections.IDictionary;
                if (list == null) cl = "★_soundList 反射取不到★";
                else if (!list.Contains(key)) cl = "_soundList(" + list.Count + " 项) 里没有 → 要走 LoadAudio 异步加载";
                else
                {
                    object ai = list[key];
                    var clips = Member(ai, "_clips") as Array;
                    cl = "Loaded=" + Member(ai, "Loaded") +
                         " clips=" + (clips == null ? "null" : clips.Length.ToString()) +
                         " clip[0]=" + ((clips != null && clips.Length > 0)
                                        ? (clips.GetValue(0) == null ? "★null★(bundle 里没取到)" : "有")
                                        : "空");
                }

                Debug.LogWarning("[CFZ-Offline][声音] ── 声音链路诊断 '" + key + "' ──\n" +
                                 "    ① 数据表: " + tbl + "\n" +
                                 "    ② 缓存/加载: " + cl);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][声音] DumpAudioState 异常: " + e.Message); }
        }

        // ==================== ★FX / 后处理 系统状态★ ====================
        //
        //  你观察到的 "SceneCamera 的 ChangeCameraBright 进游戏被关成 false":
        //     CommonOption.ApplyGraphics(SceneType.FPS)  (CommonOption.cs:423-435)
        //         SetFPSScreenEffectLevel((SCREEN_EFFECT_LEVEL)Option.Graphic.Common.ScreenEffect.Value)
        //         SetFPSEffectLevel       ((EFFECT_LEVEL)Option.Graphic.Common.Effect.Value)
        //     Option 数据缺失 → 这两个值都是 0 → LOW
        //     → SceneRenderManager.OnChangeScreenEffectLevelEvent → SceneRenderVar.SetScreenEffectLevel
        //       → ppVolume.SetPPOption(LOW) → 后处理(含 ChangeCameraBright/FlashbangEffect)被关
        //   ★所以 ChangeCameraBright=false 是 Option.Graphic 全 0 的直接后果, 不是原因。
        //   ★这个 dump 同时给出 FX 系统是否在场 + 效果等级, 用来判定"枪口火焰/弹孔"是被等级关掉还是根本没请求。

        static bool _fxDumped = false;

        static void DumpFxState()
        {
            if (_fxDumped) return;
            _fxDumped = true;
            try
            {
                var asm = typeof(CFW.GameSystem.InputManager).Assembly;

                var tFX = asm.GetType("CFW.FXSystem.FXManager");
                object fxInst = null;
                if (tFX != null) fxInst = UnityEngine.Object.FindObjectOfType(tFX);
                string fx = tFX == null ? "★类型 CFW.FXSystem.FXManager 不存在★"
                          : (fxInst == null ? "★场景里没有 FXManager★ (特效事件没人接 → 发出去也没用)"
                                            : "在场 (" + fxInst.GetType().FullName + ")");

                var tSRM = asm.GetType("CFW.SceneRender.SceneRenderManager");
                object srm = tSRM != null ? UnityEngine.Object.FindObjectOfType(tSRM) : null;
                string lvl = srm == null ? "★SceneRenderManager 不在场★"
                           : "effectLevel=" + Member(srm, "effectLevel") +
                             " ScreenEffectLevel=" + Member(srm, "ScreenEffectLevel") +
                             " useSSAO=" + Member(srm, "useSSAO");

                string opt = "?";
                try
                {
                    var g = Common.System.Option.Option.Graphic;
                    var c = g.Common;
                    opt = "Effect=" + c.Effect.Value + " ScreenEffect=" + c.ScreenEffect.Value +
                          " Model=" + c.Quality_Model.Value + " Shadow=" + c.Quality_Shadow.Value +
                          " AntiAliasing=" + c.AntiAliasing.Value + " Brightness=" + g.Brightness.Value;
                }
                catch (Exception e2) { opt = "读取异常: " + e2.Message; }

                string cb = "?";
                try
                {
                    var tCB = asm.GetType("CFW.ScreenEffects.ChangeCameraBright");
                    if (tCB != null)
                    {
                        var arr = UnityEngine.Object.FindObjectsOfType(tCB);
                        cb = arr.Length + " 个:";
                        for (int i = 0; i < arr.Length && i < 4; i++)
                        {
                            var mb = arr[i] as UnityEngine.Behaviour;
                            cb += " [enabled=" + (mb != null ? mb.enabled.ToString() : "?") +
                                  " Bright=" + Member(arr[i], "Bright") + "]";
                        }
                    }
                }
                catch { }

                Debug.LogWarning("[CFZ-Offline][特效] ── FX/后处理状态 ──\n" +
                                 "    FXManager: " + fx + "\n" +
                                 "    渲染等级: " + lvl + "\n" +
                                 "    Option.Graphic.Common: " + opt + "\n" +
                                 "    ChangeCameraBright: " + cb);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][特效] DumpFxState 异常: " + e.Message); }
        }

        // ==================== ★光标★ 游戏意图是"锁定"时强制隐藏 ====================
        //
        // CFW.GameSystem.InputManager 的原生逻辑 (InputManager.cs:374-393 → 431-447):
        //     Update():
        //         if (!isLoadingEnd || _currInputType == None) return;
        //         dicTable[(int)_currInputType].UpdateTable();     // ★ 这一行若每帧抛异常
        //         if (_currInputType != InGameUI/Chatting/WeaponBag) {
        //             _UpdateCursorState();                        //   → 这几行永远执行不到
        //             if (isEnableMove)  _UpdateMove();
        //             if (isEnableMouse) _UpdateMouse();
        //         }
        //     _UpdateCursorState(): if (CursorLocked) { Cursor.visible=false; lockState=Locked; }
        //
        //   → 只要 UpdateTable() 抛异常(或任何 UI 把 Cursor.visible 又置 true 而没解锁),
        //     光标就"藏不住" —— 这正是"鼠标未隐藏"。
        // 修复: 每帧按**游戏自己的意图位** CursorLocked 重新压一次; 游戏想解锁时不插手。

        static int _curForce = 0;
        static void CursorEnforce()
        {
            try
            {
                var im = UnityEngine.Object.FindObjectOfType<CFW.GameSystem.InputManager>();
                if (im == null) return;
                object ct = Member(im, "_currInputType");
                if (ct == null || ct.ToString() == "None") return;
                string cs = ct.ToString();
                if (cs == "InGameUI" || cs == "Chatting" || cs == "WeaponBag") return;   // 菜单/聊天/背包: 游戏要用光标
                object cl = Member(im, "CursorLocked");
                if (!(cl is bool) || !(bool)cl) return;                                  // 游戏没要求锁 → 不插手

                bool changed = false;
                if (UnityEngine.Cursor.visible) { UnityEngine.Cursor.visible = false; changed = true; }
                if (UnityEngine.Cursor.lockState != UnityEngine.CursorLockMode.Locked)
                {
                    UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Locked;
                    changed = true;
                }
                if (changed && _curForce < 5)
                {
                    _curForce++;
                    Debug.LogWarning("[CFZ-Offline][光标] 游戏意图=锁定 但光标是显示的 → 已强制隐藏 (_currInputType=" + cs + ")");
                }
            }
            catch { }
        }

        static void SetMember(object o, string name, object v)
        {
            if (o == null) return;
            try
            {
                var t = o.GetType();
                var f = t.GetField(name, F2);
                if (f != null) { f.SetValue(o, v); Debug.Log("[CFZ-Offline][强推] 写 " + name + " 成功(field)"); return; }
                var pr = t.GetProperty(name, F2);
                if (pr != null && pr.CanWrite) { pr.SetValue(o, v, null); Debug.Log("[CFZ-Offline][强推] 写 " + name + " 成功(property)"); return; }
                Debug.LogWarning("[CFZ-Offline][强推] 写 " + name + " 失败: 成员不存在");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][强推] 写 " + name + " 异常: " + e.Message); }
        }

        // 枚举对象上所有 bool 成员, 名字以 prefix 开头 —— 无需预先知道字段名
        static string BoolFlags(object o, string prefix)
        {
            if (o == null) return "(null)";
            var list = new System.Collections.Generic.List<string>();
            try
            {
                var t = o.GetType();
                var fs = t.GetFields(F2);
                for (int i = 0; i < fs.Length; i++)
                    if (fs[i].FieldType == typeof(bool) && fs[i].Name.StartsWith(prefix))
                        list.Add(fs[i].Name + "=" + fs[i].GetValue(o));
                var ps = t.GetProperties(F2);
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].PropertyType == typeof(bool) && ps[i].Name.StartsWith(prefix) && ps[i].CanRead)
                    { try { list.Add(ps[i].Name + "=" + ps[i].GetValue(o, null)); } catch { } }
            }
            catch { }
            return list.Count > 0 ? string.Join(", ", list.ToArray()) : "(无 " + prefix + "* 成员)";
        }

        static string Cnt(System.Collections.ICollection c) { return c != null ? c.Count.ToString() : "?"; }

        // 通过 IsStateActive(PlayerStateType) 判断当前状态 (枚举类型从参数反射取得, 无需知道命名空间)
        static bool IsStateActive(object player, string stateName)
        {
            try
            {
                var st = Member(player, "State");
                if (st == null) return false;
                var m = st.GetType().GetMethod("IsStateActive", F2);
                if (m == null) return false;
                var ps = m.GetParameters();
                if (ps.Length != 1) return false;
                object val = Enum.Parse(ps[0].ParameterType, stateName);
                return (bool)m.Invoke(st, new object[] { val });
            }
            catch { return false; }
        }

        static bool IsCreating(object player) { return IsStateActive(player, "Creating"); }

        static void DumpPlayer(object p, int i)
        {
            try
            {
                if (p == null) { Debug.Log("[CFZ-Offline][监视]  P" + i + " = null"); return; }
                var comp = p as UnityEngine.Component;
                var model = Member(p, "Model");
                Debug.Log("[CFZ-Offline][监视]  P" + i + " '" + (comp != null ? comp.gameObject.name : "?") +
                          "' IsMy=" + Member(Member(p, "Data"), "IsMyPlayer") +
                          " Creating=" + IsCreating(p) +
                          " CoroutineCreate!=null=" + (Member(p, "CoroutineCreate") != null) +
                          " PhysicalObject!=null=" + (Member(p, "PhysicalObject") != null) +
                          " Model!=null=" + (model != null));
                Debug.Log("[CFZ-Offline][监视]    Player: " + BoolFlags(p, "IsCreate"));
                Debug.Log("[CFZ-Offline][监视]    Model : " + BoolFlags(model, "IsCreate"));
                if (comp != null)
                    Debug.Log("[CFZ-Offline][监视]    子节点 PV=" + (comp.transform.Find("PV") != null) +
                              " QV=" + (comp.transform.Find("QV") != null) +
                              " PVCamera(全局)=" + (GameObject.Find("PVCamera") != null));
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][监视] DumpPlayer 异常: " + e.Message); }
        }

        static float _stuckSince = -1f;
        static int _forceCount = 0;

        // 周期快照 + 12 秒超时强推
        static void PlayerWatchdog()
        {
            try
            {
                if (!CFW.UI.Loading.LoadingUI.IsNowLoading)
                {
                    _stuckSince = -1f;
#if DEBUG
                    InMapWatchdog();            // 地图状态观测快照 (仅 Debug 版; 纯日志无副作用)
#endif
                    return;
                }

                if (Time.realtimeSinceStartup - _lastWatch < 3f) return;
                _lastWatch = Time.realtimeSinceStartup;

                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                if (pm == null) { Debug.Log("[CFZ-Offline][监视] PlayerManager 实例为 null"); return; }

                var dl = Refl(pm, "dicLoadingPlayers") as System.Collections.ICollection;
                var dp = Refl(pm, "dicPlayers") as System.Collections.ICollection;
                var lp = Refl(pm, "_listPlayers") as System.Collections.IList;
                Debug.Log("[CFZ-Offline][监视] 加载中=" + Cnt(dl) + " 已创建=" + Cnt(dp) + " 玩家列表=" + Cnt(lp) +
                          " | IsNowLoading=True isGameSceneLoadingComplete=" + CFW.UI.Loading.LoadingUI.isGameSceneLoadingComplete);

                // ★ Loading_End 兜底: Loading_Complete 已过 3 秒、玩家加载字典已清空, 却没人发 Loading_End → 我们自己发
                if (!_sentLoadingEnd && _loadingCompleteAt > 0f
                    && Time.realtimeSinceStartup - _loadingCompleteAt > 3f
                    && CFW.UI.Loading.LoadingUI.IsNowLoading
                    && (dl == null || dl.Count == 0))
                {
                    _sentLoadingEnd = true;
                    Debug.LogWarning("[CFZ-Offline][兜底] Loading_Complete 已 " +
                                     (Time.realtimeSinceStartup - _loadingCompleteAt).ToString("0.0") +
                                     " 秒但无人发 Loading_End → 自己派发 Loading_End (关闭加载界面)");
                    CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Loading_End, null);
                    Debug.Log("[CFZ-Offline][兜底] 派发后 IsNowLoading=" + CFW.UI.Loading.LoadingUI.IsNowLoading);
                }

                bool anyCreating = false;
                if (lp != null)
                {
                    for (int i = 0; i < lp.Count; i++)
                    {
                        DumpPlayer(lp[i], i);
                        if (IsCreating(lp[i])) anyCreating = true;
                    }

                    // ★ 12 秒仍卡在 Creating → 强推 (把 CoroutineCreate 置 null, 让 IsStateEnd() 通过)
                    if (anyCreating)
                    {
                        if (_stuckSince < 0f) { _stuckSince = Time.realtimeSinceStartup; }
                        else if (Time.realtimeSinceStartup - _stuckSince > 12f && _forceCount < 3)
                        {
                            _forceCount++;
                            Debug.LogWarning("[CFZ-Offline][强推] 玩家 Creating 卡了 " +
                                             (Time.realtimeSinceStartup - _stuckSince).ToString("0.0") + " 秒 → 清除 CoroutineCreate (第 " + _forceCount + " 次)");
                            for (int i = 0; i < lp.Count; i++)
                            {
                                if (!IsCreating(lp[i])) continue;
                                SetMember(lp[i], "CoroutineCreate", null);
                            }
                            _stuckSince = Time.realtimeSinceStartup;
                        }
                    }
                    else _stuckSince = -1f;
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][监视] 异常: " + e); }
        }

        static float _lastUIFix = -100f;
        static int _uiFixCount = 0;
        static bool _wantIngame = false;
        static float _lastDrive = -100f;

        // 自动唤醒 InGameUI: Loading_Start 可能早于 InGameUI.Start() 的 RegisterEvent, 事件被漏掉
        static void AutoFixInGameUI()
        {
            try
            {
                // ★兜底驱动★ InGameUI 已就绪但 Loading_Start 从未派发 → 我们自己调 AI_Tutorial_Loading.SetModeInfo()
                if (_wantIngame && _uiFixCount < 25
                    && CFW.UI.Loading.LoadingUI.IsLoadingUIReady
                    && CFW.UI.InGameUI.InGameUI.IsLoadComplete
                    && !CFW.UI.Loading.LoadingUI.IsNowLoading
                    && !CFW.UI.Loading.LoadingUI.isGameSceneLoadingComplete
                    && Time.realtimeSinceStartup - _lastDrive > 8f)
                {
                    _lastDrive = Time.realtimeSinceStartup;
                    Debug.Log("[CFZ-Offline][兜底] IsLoadComplete=True 但 Loading_Start 未派发 → 手动驱动 SetModeInfo");
                    if (DriveSetModeInfo()) return;
                }
                if (_uiFixCount >= 25) return;
                if (!CFW.UI.Loading.LoadingUI.IsLoadingUIReady) return;
                if (CFW.UI.InGameUI.InGameUI.IsLoadComplete) return;
                if (Time.realtimeSinceStartup - _lastUIFix < 1.5f) return;
                _lastUIFix = Time.realtimeSinceStartup;
                _uiFixCount++;
                var ig = UnityEngine.Object.FindObjectOfType<CFW.UI.InGameUI.InGameUI>();
                if (ig == null) { Debug.Log("[CFZ-Offline][自动修复] 第" + _uiFixCount + "次: InGameUI 实例为 null"); return; }
                Debug.Log("[CFZ-Offline][自动修复] 第" + _uiFixCount + "次: 尝试唤醒 InGameUI (active=" + ig.gameObject.activeInHierarchy + ", enabled=" + ig.enabled + ")");
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.InGameUI_OnLoad, CFW.Network.NetEvent.Param_LoadingStart);
                bool ran = CFW.UI.InGameUI.InGameUI.Manager != null;
                Debug.Log("[CFZ-Offline][自动修复]   事件派发后 InGameUI.Manager!=null = " + ran + " (true 表示 OnEvent_OnLoad 已被调用)");
                if (!ran)
                {
                    var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    var m = ig.GetType().GetMethod("OnEvent_OnLoad", F2);
                    if (m != null)
                    {
                        Debug.Log("[CFZ-Offline][自动修复]   直接反射调用 InGameUI.OnEvent_OnLoad");
                        m.Invoke(ig, new object[] { CFW.Framework.GameEvent.InGameUI_OnLoad, CFW.Network.NetEvent.Param_LoadingStart });
                        Debug.Log("[CFZ-Offline][自动修复]   直接调用后 Manager!=null = " + (CFW.UI.InGameUI.InGameUI.Manager != null));
                    }
                    else Debug.LogWarning("[CFZ-Offline][自动修复]   OnEvent_OnLoad 方法未找到");
                }
                Debug.Log("[CFZ-Offline][自动修复]   结果 InGameUI.IsLoadComplete = " + CFW.UI.InGameUI.InGameUI.IsLoadComplete);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][自动修复] 异常: " + e); }
        }

        IEnumerator Start()
        {
            Debug.Log("[CFZ-Offline] ===== Driver 启动 =====");
            try { Patcher.Install(); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 补丁安装失败: " + e); yield break; }

            // ---- 阶段 1: 等 LobbyNetClient 实例 ----
            float t0 = Time.realtimeSinceStartup;
            while (true)
            {
                lnc = CFW.UI.Lobby.LobbyClient.LobbyNetClient;
                if (lnc != null) break;
                if (Time.realtimeSinceStartup - t0 > 180f)
                { Debug.LogError("[CFZ-Offline] 等待 LobbyNetClient 超时"); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            yield return new WaitForSecondsRealtime(1f);

            // ---- 阶段 2: 初始化崩坏则重跑 Start() ----
            if (GetField(lnc, "connector") == null)
            {
                Debug.Log("[CFZ-Offline] LobbyNetClient 初始化不完整, 重跑 Start()");
                try { Call(lnc, "Start"); }
                catch (Exception e) { Debug.LogError("[CFZ-Offline] 重跑 Start 失败: " + e); }
            }
            if (GetField(lnc, "connector") == null)
            { Debug.LogError("[CFZ-Offline] connector 仍为 null, 放弃"); yield break; }
            Debug.Log("[CFZ-Offline] LobbyNetClient 就绪");

            // ---- 阶段 3: 预设伪造状态 ----
            try { CFW.Publisher.PublisherModule.OpenID = "OfflinePlayer"; } catch { }
            try { CFW.Publisher.PublisherModule.OpenKey = "OfflinePlayer"; } catch { }
            SetField(lnc, "isBrokerServerStateReceived", true);
            SetField(lnc, "isBrokerServerBusy", false);
            SetField(lnc, "isProcessing", false);
            SetField(GetField(lnc, "connector"), "isConnected", true);
            Debug.Log("[CFZ-Offline] 连接状态已伪造");

            // ---- 阶段 4: 修复 GameClient 初始化 (Awake 被 Stove 异常中断) ----
            t0 = Time.realtimeSinceStartup;
            while (UnityEngine.Object.FindObjectOfType<CFW.Framework.GameClient>() == null)
            {
                if (Time.realtimeSinceStartup - t0 > 180f)
                { Debug.LogError("[CFZ-Offline] 等待 GameClient 超时"); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            yield return new WaitForSecondsRealtime(1f);
            var gc = UnityEngine.Object.FindObjectOfType<CFW.Framework.GameClient>();

            // 诊断 + 修复
            try
            {
                object pmr = GetField(gc, "PublisherModuleResult");
                Debug.Log("[CFZ-Offline] GameClient 诊断: PublisherModuleResult=" + pmr +
                          ", active=" + gc.gameObject.activeInHierarchy);
                var pmrField = gc.GetType().GetField("PublisherModuleResult", F);
                if (pmrField != null && (pmr == null || pmr.ToString() != "OK"))
                {
                    pmrField.SetValue(gc, Enum.ToObject(pmrField.FieldType, 0));
                    Debug.Log("[CFZ-Offline] PublisherModuleResult 已改为 OK");
                }
                UnityEngine.Object.DontDestroyOnLoad(gc.gameObject);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] GameClient 修复异常: " + e); }

            // 补 Awake 未执行的字段: _hudFPS / GamePFCheck / currState (_SceneChangeToLobby 第一行会用到 _hudFPS)
            try
            {
                var hudField = gc.GetType().GetField("_hudFPS", F);
                if (hudField != null && hudField.GetValue(gc) == null)
                {
                    var hudComp = gc.GetComponent(hudField.FieldType);
                    if (hudComp != null)
                    {
                        hudField.SetValue(gc, hudComp);
                        Debug.Log("[CFZ-Offline] 已补 _hudFPS");
                    }
                    else Debug.LogWarning("[CFZ-Offline] HUDFPS 组件未找到, _SceneChangeToLobby 可能仍 NRE");
                }
                var pfField = gc.GetType().GetField("GamePFCheck", F);
                if (pfField != null && pfField.GetValue(gc) == null)
                {
                    var pfComp = gc.GetComponent(pfField.FieldType);
                    if (pfComp != null) pfField.SetValue(gc, pfComp);
                }
                var stField = gc.GetType().GetField("currState", F);
                if (stField != null && System.Convert.ToInt32(stField.GetValue(gc)) == 0)
                {
                    stField.SetValue(gc, Enum.ToObject(stField.FieldType, 1)); // GameState.Initialize
                    Debug.Log("[CFZ-Offline] 已补 currState = Initialize");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 补 Awake 字段异常: " + e); }

            // 重跑 Start(): 注册所有 GameEvent + 加载数据表
            try { Call(gc, "Start"); Debug.Log("[CFZ-Offline] GameClient.Start() 已重跑"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] GameClient.Start() 重跑失败: " + e); }

            // 等游戏自行推进 (数据表→InitGameData→SoundManager→自己发 Lobby_Enter)
            // ★轮询版★ ServerConnectForm 一出现就走 (游戏自己发 Lobby_Enter 通常几秒内完成),
            //   30 秒还没出现才兜底补发。原来的"死等 30 秒"是登录界面干等的主因。
            {
                float tLobby = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tLobby < 30f)
                {
                    if (UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.SceneForms.ServerConnectForm>() != null)
                    {
                        Debug.Log("[CFZ-Offline] 游戏已自行推进到登录界面 (等了 " +
                                  (Time.realtimeSinceStartup - tLobby).ToString("0.0") + " 秒)");
                        break;
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                if (UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.SceneForms.ServerConnectForm>() == null)
                {
                    Debug.Log("[CFZ-Offline] 兜底发送 Lobby_Enter (30 秒未自行推进)");
                    CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Lobby_Enter, null);
                }
            }

            // ---- 阶段 5: 等 ServerConnectForm ----
            yield return StartCoroutine(WaitFor(typeof(CFW.UI.Lobby.SceneForms.ServerConnectForm), 60f));
            var scf = waited as CFW.UI.Lobby.SceneForms.ServerConnectForm;
            if (scf == null) { Debug.LogError("[CFZ-Offline] ServerConnectForm 未出现"); yield break; }
            Debug.Log("[CFZ-Offline] ServerConnectForm 出现, 触发登录");

            // ---- 阶段 5.4: 关闭 AssetBundle (残包无 bundle 清单, 走非 bundle 资源路径) ----
            try
            {
                var rl = Common.System.ResourceLoader.Instance;
                if (rl != null)
                {
                    rl.IsUseAssetBundle = false;
                    Debug.Log("[CFZ-Offline] AssetBundle 已关闭 (IsUseAssetBundle=false)");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 关闭 AssetBundle 失败: " + e); }

            // ---- 阶段 5.5: 修复 LobbyClient.dataHandler (GetData 返回 null 的根源) + 注入 LobbyNetClient 数据字段 ----
            lc = UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.LobbyClient>();
            var dh = lc != null ? lc.GetComponent<CFW.UI.Lobby.Data.DataHandler>() : null;
            if (dh == null)
            {
                Debug.LogError("[CFZ-Offline] DataHandler 组件未找到, GetData 将返回 null");
            }
            else
            {
                try
                {
                    if (GetField(lc, "dataHandler") == null)
                    { SetField(lc, "dataHandler", dh); Debug.Log("[CFZ-Offline] 已修复 LobbyClient.dataHandler"); }
                    if (GetField(dh, "lobbyClient") == null) SetField(dh, "lobbyClient", lc);
                    try { Call(dh, "InitializeDatas"); } catch { }
                }
                catch (Exception e) { Debug.LogError("[CFZ-Offline] 修复 dataHandler 异常: " + e); }
                Inj(lnc, dh, "myUserInfo", typeof(CFW.UI.Lobby.Data.MyUserInfomation), CFW.UI.Lobby.Data.DataType.MyUserInfomation);
                Inj(lnc, dh, "myPlayInfo", typeof(CFW.UI.Lobby.Data.MyPlayInformation), CFW.UI.Lobby.Data.DataType.MyPlayInformation);
                Inj(lnc, dh, "myInven", typeof(CFW.UI.Lobby.Data.MyInventory), CFW.UI.Lobby.Data.DataType.MyInventory);
                Inj(lnc, dh, "friends", typeof(CFW.UI.Lobby.Data.Friend), CFW.UI.Lobby.Data.DataType.Friend);
                Inj(lnc, dh, "clan", typeof(CFW.UI.Lobby.Data.Clan), CFW.UI.Lobby.Data.DataType.Clan);
                Inj(lnc, dh, "searchUserList", typeof(CFW.UI.Lobby.Data.SearchUserList), CFW.UI.Lobby.Data.DataType.SearchUserList);
                Inj(lnc, dh, "channelList", typeof(CFW.UI.Lobby.Data.ChannelList), CFW.UI.Lobby.Data.DataType.ChannelList);
                Inj(lnc, dh, "roomList", typeof(CFW.UI.Lobby.Data.RoomList), CFW.UI.Lobby.Data.DataType.RoomList);
                Inj(lnc, dh, "gameRoomInfo", typeof(CFW.UI.Lobby.Data.GameRoomInfo), CFW.UI.Lobby.Data.DataType.GameRoomInfo);
                Inj(lnc, dh, "postBox", typeof(CFW.UI.Lobby.Data.PostBox), CFW.UI.Lobby.Data.DataType.PostBox);
                Inj(lnc, dh, "idip", typeof(CFW.UI.Lobby.Data.IDIP), CFW.UI.Lobby.Data.DataType.IDIP);
                Inj(lnc, dh, "party", typeof(CFW.UI.Lobby.Data.Party), CFW.UI.Lobby.Data.DataType.Party);
                Inj(lnc, dh, "profile", typeof(CFW.UI.Lobby.Data.Profile), CFW.UI.Lobby.Data.DataType.Profile);
                Inj(lnc, dh, "icSeason", typeof(CFW.UI.Lobby.Data.ICSeason), CFW.UI.Lobby.Data.DataType.ICSeason);
                Inj(lnc, dh, "myBattlePassData", typeof(CFW.UI.Lobby.Data.MyBattlePassData), CFW.UI.Lobby.Data.DataType.MyBattlePassData);
                Debug.Log("[CFZ-Offline] 数据注入完成: myUserInfo=" + (GetField(lnc, "myUserInfo") != null));

                // ---- 阶段 5.6: 初始化 Party (Init + Add 一个 mode), 让 OnClick_BT_Ready 的 if 通过 ----
                try
                {
                    var mui = GetField(lnc, "myUserInfo") as CFW.UI.Lobby.Data.MyUserInfomation;
                    var party = GetField(lnc, "myParty") as CFW.UI.Lobby.Data.Party;
                    var inven = GetField(lnc, "myInven") as CFW.UI.Lobby.Data.MyInventory;
                    if (mui != null && party != null && inven != null)
                    {
                        if (party.PartyCount == 0) { party.Init(mui, inven); Debug.Log("[CFZ-Offline] Party.Init 已调用 (PartyCount=1)"); }
                        if (party.GameMode.Count == 0) { party.GameMode.Add((byte)CFW.DataTable.Header.eMatchModeType.FPS_TEAM_DEATH_MATCH_1); Debug.Log("[CFZ-Offline] GameMode.Add(FPS_TEAM_DEATH_MATCH_1)"); }
                    }
                }
                catch (Exception e) { Debug.LogError("[CFZ-Offline] 初始化 Party 异常: " + e); }
            }

            try { Call(scf, "StopLoginProc"); } catch { }
            try { Call(scf, "LoginRequest"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] LoginRequest 调用失败: " + e); }

            // ---- 阶段 6: 喂登录链事件 ----
            yield return new WaitForSecondsRealtime(1f);
            lc = UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.LobbyClient>();
            if (lc == null) { Debug.LogError("[CFZ-Offline] 找不到 LobbyClient"); yield break; }
            var R0 = new CFW.UI.Lobby.Event.Recv_Result(0);
            Debug.Log("[CFZ-Offline] 派发登录链事件");
            Send(CFW.UI.Lobby.Event.UIEventType.LoginAck, new CFW.UI.Lobby.Event.Recv_LoginAckParam(0, 0, ""));
            Send(CFW.UI.Lobby.Event.UIEventType.GameTypeOpenAck, new CFW.UI.Lobby.Event.Recv_GameTypeOpenAck(true, true, true));
            Send(CFW.UI.Lobby.Event.UIEventType.ShopDisableItemListAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.UserInfoAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.MysteryShopInfoAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.PostListAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.FriendListAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.MyClanInformationChanged, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.DailyMissionAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.GachaInfoAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.PurchaseCountAck, R0);
            Debug.Log("[CFZ-Offline] 登录链事件已派发, 等待 AutoConfig");

            // ---- 阶段 7: 等 AutoConfigForm ----
            yield return StartCoroutine(WaitFor(typeof(CFW.UI.Lobby.SceneForms.AutoConfigForm), 60f));
            var acf = waited as CFW.UI.Lobby.SceneForms.AutoConfigForm;
            if (acf == null) { Debug.LogError("[CFZ-Offline] AutoConfigForm 未出现 (登录链可能中断)"); yield break; }
            Debug.Log("[CFZ-Offline] AutoConfigForm 出现");

            try
            {
                var mui = GetField(lnc, "myUserInfo") as CFW.UI.Lobby.Data.MyUserInfomation;
                if (mui == null && dh != null)
                    mui = dh.GetData<CFW.UI.Lobby.Data.MyUserInfomation>(CFW.UI.Lobby.Data.DataType.MyUserInfomation);
                if (mui != null)
                {
                    mui.UserData.NickName = "OfflinePlayer";
                    mui.AutoConfigData = new CFW.UI.Lobby.Data.AutoConfigData();
                    mui.AutoConfigData.Initialize();
                    mui.AutoConfigData.GraphicMemory = UnityEngine.SystemInfo.graphicsMemorySize;
                    Debug.Log("[CFZ-Offline] 用户数据已预设 (昵称 + 显卡配置)");
                }
                else Debug.LogError("[CFZ-Offline] myUserInfo 仍为 null");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 预设用户数据失败: " + e); }

            // AutoConfigAck(走"配置匹配+昵称有效"分支 → ChangeFlowToGameSelect) + _RequestProcess 等待的三个 Ack
            Send(CFW.UI.Lobby.Event.UIEventType.AutoConfigAck, null);
            Send(CFW.UI.Lobby.Event.UIEventType.BattlePassAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.SkinListAck, R0);
            Send(CFW.UI.Lobby.Event.UIEventType.ItemUnlockInformationAck, R0);
            Debug.Log("[CFZ-Offline] ===== 全部完成, 应已进入模式选择界面 =====");

            // ---- 阶段 8: 诊断 MatchingForm (点 FPS 后出现, BundleCheck NRE 定位) ----
            Debug.Log("[CFZ-Offline] 开始监测 MatchingForm");
            t0 = Time.realtimeSinceStartup;
            bool mfDiag = false;
            while (!mfDiag && Time.realtimeSinceStartup - t0 < 600f)
            {
                var mf = UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.SceneForms.MatchingForm>();
                if (mf != null)
                {
                    mfDiag = true;
                    Debug.Log("[CFZ-Offline] ===== MatchingForm 诊断 =====");
                    Debug.Log("  myUserInfo=" + (GetField(mf, "myUserInfo") != null) + " myPlayInfo=" + (GetField(mf, "myPlayInfo") != null) + " myParty=" + (GetField(mf, "myParty") != null));
                    Debug.Log("  lobbyClient=" + (GetField(mf, "lobbyClient") != null) + " lobbyNetClient=" + (GetField(mf, "lobbyNetClient") != null));
                    Debug.Log("  BT_Ready=" + (GetField(mf, "BT_Ready") != null) + " BT_ReadyCancel=" + (GetField(mf, "BT_ReadyCancel") != null) + " Pn_Matching=" + (GetField(mf, "Pn_Matching") != null) + " Txt_Matching=" + (GetField(mf, "Txt_Matching") != null));
                    Debug.Log("  PG_DownLoading=" + (GetField(mf, "PG_DownLoading") != null) + " Pn_DownLoadComplete=" + (GetField(mf, "Pn_DownLoadComplete") != null) + " Pn_DownLoadingAlert=" + (GetField(mf, "Pn_DownLoadingAlert") != null) + " Anim1=" + (GetField(mf, "DownLoadCompleteAnimation_1") != null) + " Anim2=" + (GetField(mf, "DownLoadCompleteAnimation_2") != null));
                    Debug.Log("  SimplePartyUI=" + (GetField(mf, "SimplePartyUI") != null) + " PartyNameTagList=" + (GetField(mf, "PartyNameTagList") != null));
                    var mu = GetField(mf, "myUserInfo");
                    Debug.Log("  myUserInfo.BundleCheck=" + (mu != null && GetField(mu, "BundleCheck") != null));
                    Debug.Log("  ResourceLoader.Instance=" + (Common.System.ResourceLoader.Instance != null) + " IsUseAssetBundle=" + (Common.System.ResourceLoader.Instance != null ? Common.System.ResourceLoader.Instance.IsUseAssetBundle.ToString() : "n/a"));
                    try
                    {
                        var cfg = CFW.Framework.Connection.ServerConnector.Config;
                        Debug.Log("  ServerConnector.Config.IsRealServer=" + GetAny(cfg, "IsRealServer"));
                    }
                    catch { }
                    // 修复: 注入缺失的数据字段
                    if (dh != null)
                    {
                        if (GetField(mf, "myUserInfo") == null) SetField(mf, "myUserInfo", dh.GetData<CFW.UI.Lobby.Data.MyUserInfomation>(CFW.UI.Lobby.Data.DataType.MyUserInfomation));
                        if (GetField(mf, "myPlayInfo") == null) SetField(mf, "myPlayInfo", dh.GetData<CFW.UI.Lobby.Data.MyPlayInformation>(CFW.UI.Lobby.Data.DataType.MyPlayInformation));
                        if (GetField(mf, "myParty") == null) SetField(mf, "myParty", dh.GetData<CFW.UI.Lobby.Data.Party>(CFW.UI.Lobby.Data.DataType.Party));

                        // 阶段 8.1: 防御性初始化 Party (Init + Add mode), 让 OnClick_BT_Ready 的 if 通过
                        try
                        {
                            var mui2 = GetField(mf, "myUserInfo") as CFW.UI.Lobby.Data.MyUserInfomation;
                            var party2 = GetField(mf, "myParty") as CFW.UI.Lobby.Data.Party;
                            var inven2 = GetField(mf, "myInven") as CFW.UI.Lobby.Data.MyInventory;
                            if (mui2 != null && party2 != null && inven2 != null)
                            {
                                if (party2.PartyCount == 0) { party2.Init(mui2, inven2); Debug.Log("[CFZ-Offline] MatchingForm 阶段: Party.Init"); }
                                if (party2.GameMode.Count == 0) { party2.GameMode.Add((byte)CFW.DataTable.Header.eMatchModeType.FPS_TEAM_DEATH_MATCH_1); Debug.Log("[CFZ-Offline] MatchingForm 阶段: GameMode.Add"); }
                            }
                        }
                        catch (Exception ex) { Debug.LogWarning("[CFZ-Offline] MatchingForm Party 补初始化异常: " + ex.Message); }
                    }
                }

                // 阶段 8.2: 守护 Party.GameMode.Count (防 MatchingForm.StartForm 清空), 直到玩家点 Ready/StateChange
                yield return new WaitForSecondsRealtime(0.5f);
                int guardLog = 0;
                while (Time.realtimeSinceStartup - t0 < 1500f)
                {
                    var mf2 = UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.SceneForms.MatchingForm>();
                    var rf2 = mf2 ?? (UnityEngine.MonoBehaviour)UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.SceneForms.CustomRoomForm>();
                    if (rf2 != null)
                    {
                        var p2 = GetField(rf2, "myParty") as CFW.UI.Lobby.Data.Party;
                        var mu2 = GetField(rf2, "myUserInfo") as CFW.UI.Lobby.Data.MyUserInfomation;
                        var iv2 = GetField(rf2, "myInven") as CFW.UI.Lobby.Data.MyInventory;
                        if (p2 != null && mu2 != null && iv2 != null)
                        {
                            if (p2.PartyCount == 0) { try { p2.Init(mu2, iv2); } catch { } }
                            if (p2.GameMode.Count == 0 && guardLog < 3) { try { p2.GameMode.Add((byte)CFW.DataTable.Header.eMatchModeType.FPS_TEAM_DEATH_MATCH_1); Debug.Log("[CFZ-Offline] 守护: GameMode 补回 (被 MatchingForm.StartForm.Clear 了)"); guardLog++; } catch { } }
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
        }

        void Send(CFW.UI.Lobby.Event.UIEventType e, CFW.UI.Lobby.Event.IEventParam p)
        {
            try { lc.SendUIEvent(e, p); }
            catch (Exception ex) { Debug.LogError("[CFZ-Offline] 事件 " + e + " 异常: " + ex.Message); }
        }

        public static void TriggerInGamePostfix(UnityEngine.MonoBehaviour __instance)
        {
            try
            {
                Debug.Log("[CFZ-Offline] postfix 触发: " + __instance.GetType().FullName);
                DoTriggerInGame(__instance);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] TriggerInGamePostfix 异常: " + e); }
        }

#if DEBUG
        public static void ButtonPressPrefix(UnityEngine.UI.Button __instance)
        {
            try { LogButtonClick(__instance); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ButtonPressPrefix 异常: " + e); }
        }

        static void LogButtonClick(UnityEngine.UI.Button btn)
        {
            var path = GetTransformPath(btn.transform);
            var binds = GetButtonBindings(btn);
            string extra = "";
            try
            {
                var lc = UnityEngine.Object.FindObjectOfType<CFW.UI.Lobby.LobbyClient>();
                if (lc != null) extra = " | IsProcessing=" + GetAny(lc, "IsProcessing");
            }
            catch { }
            Debug.Log("[CFZ-Offline] >>> 按钮点击: " + path + " | 绑定: " + binds + extra);
        }

        static string GetTransformPath(UnityEngine.Transform tr)
        {
            if (tr == null) return "?";
            var sb = new System.Text.StringBuilder(tr.name);
            var p = tr.parent;
            int depth = 0;
            while (p != null && depth < 24) { sb.Insert(0, p.name + "/"); p = p.parent; depth++; }
            return sb.ToString();
        }

        static string GetButtonBindings(UnityEngine.UI.Button btn)
        {
            try
            {
                var onClick = btn.onClick;
                var sb = new System.Text.StringBuilder();
                object callsObj = null;
                for (var t = onClick.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null) { callsObj = f.GetValue(onClick); break; }
                }
                if (callsObj != null)
                {
                    System.Collections.IList rt = null;
                    for (var t = callsObj.GetType(); t != null; t = t.BaseType)
                    {
                        var f = t.GetField("m_RuntimeCalls", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null) { rt = f.GetValue(callsObj) as System.Collections.IList; break; }
                    }
                    if (rt != null)
                    {
                        foreach (var c in rt)
                        {
                            if (sb.Length > 0) sb.Append(" ; ");
                            sb.Append(DescribeInvokable(c));
                        }
                    }
                }
                int pc = onClick.GetPersistentEventCount();
                for (int i = 0; i < pc; i++)
                {
                    if (sb.Length > 0) sb.Append(" ; ");
                    var tg = onClick.GetPersistentTarget(i);
                    sb.Append("[P]" + (tg != null ? tg.GetType().Name : "null") + "." + onClick.GetPersistentMethodName(i));
                }
                if (sb.Length == 0) sb.Append("(无绑定)");
                return sb.ToString();
            }
            catch (Exception e) { return "(err " + e.Message + ")"; }
        }

        static string DescribeInvokable(object call)
        {
            if (call == null) return "?";
            for (var t = call.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo[] fs;
                try { fs = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); }
                catch { continue; }
                foreach (var f in fs)
                {
                    object v;
                    try { v = f.GetValue(call); } catch { continue; }
                    var d = v as System.Delegate;
                    if (d != null)
                    {
                        string tn = d.Target != null ? d.Target.GetType().Name : "<static>";
                        return tn + "." + d.Method.Name;
                    }
                }
            }
            return call.GetType().Name;
        }
#endif

        // 普通方法 (Harmony 不编译内部), 所有反射都在这里
        static void DoTriggerInGame(object form)
        {
            if (form == null) return;
            var F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var t = form.GetType();
            var lcF = t.GetField("lobbyClient", F); var lc = lcF != null ? lcF.GetValue(form) : null;
            if (lc == null) { Debug.LogWarning("[CFZ-Offline] lobbyClient null"); return; }
            var muF = t.GetField("myUserInfo", F); var mu = muF != null ? muF.GetValue(form) : null;
            if (mu == null) { Debug.LogWarning("[CFZ-Offline] myUserInfo null"); return; }
            var udF = mu.GetType().GetField("UserData", F); var userData = udF != null ? udF.GetValue(mu) : null;
            if (userData == null) { Debug.LogWarning("[CFZ-Offline] UserData null"); return; }
            long userKey = (long)userData.GetType().GetField("UserKey", F).GetValue(userData);
            string nick = userData.GetType().GetField("NickName", F).GetValue(userData) as string;
            if (string.IsNullOrEmpty(nick)) nick = "Player";

            var pF = t.GetField("myParty", F); var party = pF != null ? pF.GetValue(form) : null;
            if (party != null)
            {
                var getM = party.GetType().GetMethod("GetPartybyUserkey", F);
                if (getM != null)
                {
                    var pd = getM.Invoke(party, new object[] { userKey });
                    if (pd != null)
                    {
                        var ustF = pd.GetType().GetField("userState", F);
                        if (ustF != null) ustF.SetValue(pd, (byte)1);
                    }
                }
                var isMF = party.GetType().GetField("isMatching", F);
                if (isMF != null) isMF.SetValue(party, true);
            }

            LogScenes();
            EnterTutorialAI();
        }

        // 准备 GameClient 状态, 让 OnEvent_EnterTutorial / Tutorial_AI / InGame 的守卫通过
        static void PrepGameClient()
        {
            var gc = UnityEngine.Object.FindObjectOfType<CFW.Framework.GameClient>();
            if (gc == null) { Debug.LogError("[CFZ-Offline] GameClient 未找到"); return; }
            var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = gc.GetType();
            try { var f = t.GetField("currState", F2); if (f != null) f.SetValue(gc, Enum.ToObject(f.FieldType, 2)); } catch { }   // GameState.Lobby
            try { var f = t.GetField("isNowSceneChanging", F2); if (f != null) f.SetValue(gc, false); } catch { }
            try { var f = t.GetField("isNowSceneChangingInGame", F2); if (f != null) f.SetValue(gc, false); } catch { }
            try { var f = t.GetField("ingameSceneName", F2); if (f != null) Debug.Log("[CFZ-Offline] ingameSceneName = '" + f.GetValue(gc) + "'"); } catch { }
        }

        public static void EnterInGame()
        {
            try { PrepGameClient(); CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.InGame_Enter, null); Debug.Log("[CFZ-Offline] >>> GameEvent.InGame_Enter (真实 InGame 场景, 无 AI)"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] EnterInGame 异常: " + e); }
        }

        public static void EnterTutorial()
        {
            try { PrepGameClient(); CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Tutorial_Enter, null); Debug.Log("[CFZ-Offline] >>> GameEvent.Tutorial_Enter (Tutorial 场景, 自带完整单机数据)"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] EnterTutorial 异常: " + e); }
        }

        public static void EnterTutorialAI()
        {
            try { PrepGameClient(); CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Tutorial_AI_Enter, null); Debug.Log("[CFZ-Offline] >>> GameEvent.Tutorial_AI_Enter (InGame 地图 + AI 机器人)"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] EnterTutorialAI 异常: " + e); }
        }

        // F9 / 模式选择界面 [AI Combat] 按钮共用的进图入口: 复位全部局内状态 + 重读 ini + 进 AI 图
        public static void EnterAICombat()
        {
            _wantIngame = true;
            _mapWatchCount = 0; _lastWatch2 = -100f;
            _sentLoadingEnd = false; _loadingCompleteAt = -1f;
            _respParams.Clear(); _triedRespawn = false; _replayingRespawn = false; _forceRespawnCount = 0; _inputKickCount = 0;
            _inputUpdateCount = 0; _keymapChecked = false; _keymapBroken = false; _ourMoveDir = 0;
            _moveBypassCount = 0; _moveBypassErrCount = 0; _velClampCount = 0;
            _camAttachTry = 0; _camAttachErr = 0; _camAttachDone = false;
            _roundStartFrames = 0; _roundStartSent = 0; _roundStartErr = 0;
            _audioFixed = false; _runtimeFrame = 0; _runtimeErr = 0; _freecamKills = 0;
            _pvProbeFrame = 0; _rotEnableLogged = false; _fpvTry = 0; _playerProbeFrame = 0;
            _pvShown = false; _fpvChgLog = 0;
            _soundFixed = false; _soundProbeFrame = 0; _soundListenerDone = false;
            _audioEvt = 0; _fxReq = 0; _hitFx = 0;
            _sndPlay = 0; _fxDumped = false;
            _visFrame = 0; _visDead = false; _visLog = 0;                    // ★ 模型可见性状态复位
            _graphicFixed = false; _graphicProbeFrame = 0;                   // ★ 亮度修复状态复位
            _jobLog = 0; _jobNull = 0; _jobCh = 0; _lobbySubst = 0;          // ★ bundle/FX/大厅角色探针计数复位
            _fxDump = 0; _fxIn = 0;                                          // ★ FX 渲染 dump / 进入计数复位
            ModConfig.EnsureAndLoad();                    // ★ 每次进图都重读一遍键位表
            Loadout.Load();                               // ★ 顺便重读角色/武器配置
            EnterTutorialAI();
        }

        // [AI Combat] 按钮点击 (GameSelectForm.BT_AX_CommingSoon 接管)
        public static void OnAiCombatClick()
        {
            Debug.Log("[CFZ-Offline] [AI Combat] 按钮点击 -> Tutorial_AI (等效 F9)");
            EnterAICombat();
        }

        // GameSelectForm.StartForm postfix: 接管 BT_AX_CommingSoon 按钮 + 改 Tx_CommingSoon 文本
        public static void GameSelectFormStartPostfix(object __instance)
        {
            try
            {
                var frm = __instance as UnityEngine.Component;
                if (frm == null) return;

                // 字段路径 (游戏 layout.FindControl 注册表里没有这个按钮, 字段是 null —— 游戏自己也判了空)
                var bf = __instance.GetType().GetField("BT_AX_CommingSoon",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var bt = (bf != null ? bf.GetValue(__instance) : null) as UnityEngine.UI.Button;

                // ★兜底: 直接在界面子树里按名字找 Button (点击日志证实物体存在:
                //   Canvas/SceneFlow/GameSelectForm(Clone)/GameSelectLayout(Clone)/Pn_GameSelect/BT_AX_CommingSoon)
                if (bt == null)
                {
                    foreach (var b in frm.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                        if (b != null && b.name == "BT_AX_CommingSoon") { bt = b; break; }
                }
                if (bt == null) { Debug.LogWarning("[CFZ-Offline][AI按钮] 界面子树里也没找到 BT_AX_CommingSoon"); return; }

                bt.onClick.RemoveAllListeners();                              // 游戏原本就没绑它, 清的只有我们自己重复绑的
                bt.onClick.AddListener(new UnityEngine.Events.UnityAction(OnAiCombatClick));

                // ★"Coming Soon"按钮大概率是禁用态: uGUI Press() 里 IsInteractable() 不过 → onClick 永远不执行
                //   (点击日志能打出来是因为探针挂在 Press 入口, 在交互检查之前)。强制可交互:
                bool wasOff = !bt.IsInteractable();
                bt.interactable = true;

                // 文本: Tx_CommingSoon 名字对不上 → 按 名字含 Tx_ 或 现文本含 Coming 匹配, 全部改掉
                bool txt = false;
                var dump = new System.Text.StringBuilder();
                foreach (var t in bt.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                {
                    dump.Append(t.name).Append("(Text:'").Append(t.text).Append("') ");
                    if (t.name.Contains("Tx_CommingSoon") ||
                        (t.text != null && t.text.ToLower().Contains("coming")))
                    { t.text = "AI Combat"; txt = true; dump.Append("→已改 "); }
                }
                Debug.Log("[CFZ-Offline][AI按钮] 已接管: 原禁用态=" + wasOff +
                          " | 文本" + (txt ? "已改为 AI Combat" : "未匹配") + " | 子物体: " + dump);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][AI按钮] 接管失败: " + e.Message); }
        }

        public static bool SkipPrefix()
        {
            return false;
        }

        // 追踪 SceneManager.LoadScene / LoadSceneAsync 的实参
        public static bool SceneLoadPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string s = "[CFZ-Offline] ★ SceneLoad " + (__originalMethod != null ? __originalMethod.Name : "?") + "(";
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                        s += (i > 0 ? ", " : "") + (__args[i] == null ? "null" : __args[i].ToString());
                }
                s += ")";
                Debug.Log(s);
            }
            catch { }
            return true;
        }

        public static void GetAllScenePathsPostfix(ref string[] __result)
        {
            try
            {
                if (__result == null) { Debug.Log("[CFZ-Offline] ★ AssetBundle.GetAllScenePaths = null"); return; }
                // ★ 地图自检用: 从场景包里的 .unity 路径反推"这次真正加载的是哪张图"
                //   (游戏自己的加载路径不走我的接管分支, 所以这里也记一份)
                if (__result.Length > 0 && !string.IsNullOrEmpty(__result[0]))
                {
                    try { Loadout.LoadedSceneName = System.IO.Path.GetFileNameWithoutExtension(__result[0]); } catch { }
                }
                Debug.Log("[CFZ-Offline] ★ AssetBundle.GetAllScenePaths 共 " + __result.Length + " 个:");
                for (int i = 0; i < __result.Length; i++) Debug.Log("      [" + i + "] '" + __result[i] + "'");
            }
            catch { }
        }

        // 按名字在已加载程序集里找类型 (MapData 之类的命名空间不确定)
        public static Type FindTypeAnywhere(string simpleName)
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try
                    {
                        var ts = asms[i].GetTypes();
                        for (int k = 0; k < ts.Length; k++)
                            if (ts[k].Name == simpleName) return ts[k];
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ==================== bundle 清单本地化 ====================

        public static string BundleDir()
        {
            return Application.dataPath + "/../Bundle";
        }

        public static void RegenerateBundleList()
        {
            GenerateBundleListJson(BundleDir(), BundleDir() + "/AssetBundleList.json");
        }

        // 热重载: 把磁盘上的 AssetBundleList.json 重新读进正在运行的 bundleList
        public static void ReloadBundleListLive()
        {
            try
            {
                var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var rl = Common.System.ResourceLoader.Instance;
                object bl = null;
                var p = rl.GetType().GetProperty("bundleLoader", F2);
                if (p != null) bl = p.GetValue(rl, null);
                if (bl == null) { var f = rl.GetType().GetField("bundleLoader", F2); if (f != null) bl = f.GetValue(rl); }
                if (bl == null) { Debug.LogError("[CFZ-Offline] F12: bundleLoader 未找到"); return; }
                var blp = bl.GetType().GetProperty("bundleList", F2);
                object blist = blp != null ? blp.GetValue(bl, null) : null;
                if (blist == null) { Debug.LogError("[CFZ-Offline] F12: bundleList 为 null"); return; }
                var lf = blist.GetType().GetMethod("LoadFromFile", F2, null, Type.EmptyTypes, null);
                bool ok = lf != null && (bool)lf.Invoke(blist, null);
                var am = blist.GetType().GetMethod("_AnalyzeBundleData", F2);
                object r = am != null ? am.Invoke(blist, null) : null;
                Debug.Log("[CFZ-Offline] F12: 重新加载清单 ok=" + ok + ", _AnalyzeBundleData=" + r);
                var im = blist.GetType().GetMethod("IsBundle", F2, null, new Type[] { typeof(string) }, null);
                if (im != null)
                    Debug.Log("[CFZ-Offline] F12:   IsBundle('transportship_ren')=" + im.Invoke(blist, new object[] { "transportship_ren" }));
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ReloadBundleListLive 异常: " + e); }
        }

        public static void GenerateBundleListJson(string bundleDir, string outPath)
        {
            string[] types = { "Scene", "Character_Lobby", "Character_InGame_PV", "Character_InGame_QV",
                "Weapon_Lobby_PV", "Weapon_Lobby_QV", "Weapon_InGame_PV", "Weapon_InGame_QV",
                "Equip_Lobby", "Equip_InGame", "LobbyUI", "InGameUI", "Image", "Sound", "Binary",
                "Effect", "BR", "Ax", "Character", "Weapon" };
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"RevCode\": 1,\n");
            var seen = new System.Collections.Generic.HashSet<string>();
            int total = 0;
            for (int t = 0; t < types.Length; t++)
            {
                string tn = types[t];
                int ev;
                try { ev = (int)System.Enum.Parse(typeof(Common.System.AssetBundleType), tn); }
                catch { ev = -1; }
                sb.Append("  \"").Append(tn).Append("\": [");
                if (ev >= 0)
                {
                    string sub = bundleDir + "/" + tn;
                    string[] files = System.IO.Directory.Exists(sub)
                        ? System.IO.Directory.GetFiles(sub, "*.unity3d", System.IO.SearchOption.AllDirectories)
                        : new string[0];
                    System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase);
                    bool first = true;
                    for (int i = 0; i < files.Length; i++)
                    {
                        string nm = System.IO.Path.GetFileNameWithoutExtension(files[i]).ToLower();
                        bool isScene = (tn == "Scene");
                        bool derived = !isScene && tn != "BR" && tn != "Ax";
                        string key = derived ? (tn + "/" + nm) : nm;
                        if (!seen.Add(key)) continue;
                        string rp = isScene
                            ? ("Assets/LevelResources/" + nm + "/" + nm + ".unity")
                            : ("Assets/Game Assets/_ResourcesToAssetBundle/" + tn + "/" + nm + ".prefab");
                        long size = 0;
                        try { size = new System.IO.FileInfo(files[i]).Length; } catch { }
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\n    {\"Name\":\"").Append(nm)
                          .Append("\",\"BundleType\":").Append(ev)
                          .Append(",\"ResourceName\":\"").Append(nm)
                          .Append("\",\"ResourcePath\":[\"").Append(rp)
                          .Append("\"],\"Version\":1,\"BundleFileCRC\":\"\",\"HashString\":\"\",\"BundleFileSize\":").Append(size)
                          .Append(",\"IsBundleEncrypt\":false,\"BundleVariant\":\"\"}");
                        total++;
                    }
                }
                sb.Append("\n  ]").Append(t < types.Length - 1 ? ",\n" : "\n");
            }
            sb.Append("}\n");
            try
            {
                System.IO.File.WriteAllText(outPath, sb.ToString());
                Debug.Log("[CFZ-Offline] ★ 已生成 " + outPath + ", 共 " + total + " 条 bundle");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] 写 AssetBundleList.json 失败: " + e); }
        }

        // ① 把 LoadFromURL(服务器) 换成 LoadFromFile(本地 Bundle/AssetBundleList.json)
        public static bool BundleListLoadFromURLPrefix(Common.System.BundleList __instance, string protocol, string url, Action<bool> onContinueProcess)
        {
            try
            {
                string dir = BundleDir();
                string json = dir + "/AssetBundleList.json";
                if (!System.IO.File.Exists(json)) GenerateBundleListJson(dir, json);
                if (System.IO.File.Exists(json) && __instance.LoadFromFile())
                {
                    Debug.Log("[CFZ-Offline] ★ 本地 bundle 清单加载成功: " + json);
                    // ★关键★ LoadFromFile 只填 bundleData, 不会构建 bundleChart —— 必须手动调用 _AnalyzeBundleData()
                    var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    var am = __instance.GetType().GetMethod("_AnalyzeBundleData", F2);
                    if (am != null)
                    {
                        object r = am.Invoke(__instance, null);
                        Debug.Log("[CFZ-Offline] ★ 已调用 _AnalyzeBundleData() -> " + r);
                    }
                    else Debug.LogWarning("[CFZ-Offline] _AnalyzeBundleData 未找到");
                    try
                    {
                        var im = __instance.GetType().GetMethod("IsBundle", F2, null, new Type[] { typeof(string) }, null);
                        string[] vp = { "transportship_ren", "dust2", "tutorial_1" };
                        for (int i = 0; i < vp.Length; i++)
                            Debug.Log("[CFZ-Offline]   验证 IsBundle('" + vp[i] + "')=" + (im != null ? im.Invoke(__instance, new object[] { vp[i] }).ToString() : "?"));
                    }
                    catch (Exception e2) { Debug.LogWarning("[CFZ-Offline] 验证 IsBundle 失败: " + e2.Message); }
                    if (onContinueProcess != null) onContinueProcess(true);
                    return false;
                }
                Debug.LogWarning("[CFZ-Offline] 本地清单加载失败, 回退原逻辑 " + protocol + "://" + url);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] BundleListLoadFromURLPrefix 异常: " + e); }
            return true;
        }

        public static readonly System.Collections.Generic.HashSet<string> _loggedBundles =
            new System.Collections.Generic.HashSet<string>();

        // ② 让 bundle 加载直接认本地文件, 不走网络下载
        public static bool BundleDownloadPrefix(Common.System.BundleLoadJob job, Common.System.BundleLoader __instance,
            ref Common.System.BundleLoader.BundleDownloadState __result)
        {
            try
            {
                var bl = __instance.bundleList;
                if (bl == null || job == null) { Debug.LogWarning("[CFZ-Offline] _Download: bundleList 为 null!"); return true; }
                string dir = bl.FileLocation + "/" + job.BundleInfo.BundleType;
                string fn = System.IO.Path.ChangeExtension(job.BundleInfo.Name.ToLower(), ".unity3d");
                job.DownloadPath = dir;
                job.FileName = fn;
                bool exist = System.IO.File.Exists(dir + "/" + fn);
                if (_loggedBundles.Add("DL:" + job.BundleName))
                    Debug.Log("[CFZ-Offline] ★ _Download('" + job.BundleName + "') 类型=" + job.BundleInfo.BundleType +
                              " 路径='" + dir + "/" + fn + "' 存在=" + exist);
                if (exist)
                {
                    __result = Common.System.BundleLoader.BundleDownloadState.DownloadComplete;
                    return false;
                }
                Debug.LogWarning("[CFZ-Offline] bundle 文件不存在: " + dir + "/" + fn);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] BundleDownloadPrefix 异常: " + e); }
            return true;
        }

        // _Loading 协程被 StartCoroutine 的瞬间
        public static void LoadingCoroutinePrefix(CFW.UI.Loading.LoadingUI __instance)
        {
            try { Debug.Log("[CFZ-Offline] ★ LoadingUI._Loading 协程启动"); } catch { }
        }

        public static void LoadingStartPostfix()
        {
            try
            {
                Debug.Log("[CFZ-Offline] ★ LoadingUI.OnEvent_LoadingStart 被调用, IsNowLoading=" + CFW.UI.Loading.LoadingUI.IsNowLoading);
                // ★ 兜底: F7(真实地图)/F8(教学) 这些不走 AI_Tutorial_Loading.SetModeInfo 的入口,
                //   只要进图的数据过 LoadingUI, 在这里再改写一次 (同一份配置, 幂等)
                Loadout.Apply();
            }
            catch { }
        }

        // UISet.SetProgress 轨迹 (只打变化的值, 防刷屏)
        static float _lastProgress = -999f;

        public static bool UISetProgressPrefix(float rate, MethodBase __originalMethod)
        {
            try
            {
                if (Math.Abs(rate - _lastProgress) > 0.001f)
                {
                    _lastProgress = rate;
                    Debug.Log("[CFZ-Offline] ★ UISet.SetProgress(" + rate.ToString("0.000") + ")  ← _Loading 进度" +
                              (__originalMethod != null ? " [" + __originalMethod.DeclaringType.Name + "]" : ""));
                }
            }
            catch { }
            return true;
        }

        public static void PatchUISetProgress(Harmony harmony, Type t, HarmonyMethod pre)
        {
            if (t == null) { Debug.LogWarning("[CFZ-Offline] UISet 类型未找到"); return; }
            var m = t.GetMethod("SetProgress", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(float) }, null);
            if (m != null)
            {
                harmony.Patch(m, prefix: pre);
                Debug.Log("[CFZ-Offline] 已 hook " + t.Name + ".SetProgress (定义在 " + m.DeclaringType.Name + ")");
            }
            else Debug.LogWarning("[CFZ-Offline] " + t.Name + ".SetProgress 未找到");
        }

        // ==================== 事件流 / 异常 追踪 ====================

#if DEBUG
        public static bool IsWatchedEvent(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("Input_Set", StringComparison.Ordinal)) return true;   // 输入使能类, 精确追踪
            // 屏蔽每帧刷屏的事件
            if (n == "Camera_Transform" || n == "Camera_Shake" || n == "Network_SendSync"
                || n == "InGameUI_FallDownDamage" || n == "Camera_ChangeViewTarget") return false;
            // ★特效派发追踪: FXPlayer.MakeParticleFX → StaticInstance<GameEventManager>.SendEvent(PlayEffect, p)
            //   出现 → 事件发出来了(问题在 FXManager/资源); 不出现 → fxName 为空(材质表)或上游没调
            if (n == "PlayEffect") return true;
            return n.IndexOf("Loading", StringComparison.Ordinal) >= 0
                || n.IndexOf("Tutorial_AI", StringComparison.Ordinal) >= 0
                || n.IndexOf("LockCursor", StringComparison.Ordinal) >= 0
                || n.IndexOf("GameMode", StringComparison.Ordinal) >= 0
                || n.IndexOf("InGameData", StringComparison.Ordinal) >= 0
                || n.IndexOf("InGameUI_OnLoad", StringComparison.Ordinal) >= 0
                || n.IndexOf("Camera", StringComparison.Ordinal) >= 0
                || n.IndexOf("Player", StringComparison.Ordinal) >= 0;
        }
#endif

        public static bool SendEventPrefix(CFW.Framework.GameEvent gameEvent, CFW.Framework.IGameEventParam param)
        {
            try
            {
                // ★离线换背包回环★ B 键面板选袋 → InGameUI.OnSelectWeaponBag 发 Network_SendChangeWeaponBag,
                //   在线要发给服务器再等回包; 离线假代理没人回 → 这里直接替服务器调
                //   PlayerManager.RecvWeaponBagChange (public, 游戏自己的落地函数: 换袋+换枪+刷UI)。
                if (gameEvent == CFW.Framework.GameEvent.Network_SendChangeWeaponBag)
                {
                    try
                    {
                        var cb = param as CFW.Framework.EventParam_ChangeWeaponBag;
                        var pm = CFW.InGame.Player.PlayerManager.Instance;
                        var my = (pm != null) ? pm.MyPlayer : null;
                        if (cb != null && my != null && my.Data != null)
                        {
                            cb.PlayerID = my.Data.PlayerID;
                            pm.RecvWeaponBagChange(cb);
                            if (_bagChangeLog++ < 8)
                                Debug.Log("[CFZ-Offline][背包] 已切换到背包 " + (cb.BagIndex + 1) + " (离线直连)");
                            return false;       // 已处理完, 不再走 NetClient 的假代理
                        }
                    }
                    catch (System.Exception e) { Debug.LogWarning("[CFZ-Offline][背包] 换袋失败: " + e.Message); }
                }

                // ★复活保持已选背包★ 教学模式发 Network_RecvPlayerRespawn 时 SelectBagIndex 写死 0
                //   → Player.OnRespawn(:759) 拿它重置武器袋 → 死一次就被换回背包1。
                //   在事件到达 PlayerManager 之前, 把它改成我当前选中的袋 (Data.Inventory.CurrentBag)。
                if (gameEvent == CFW.Framework.GameEvent.Network_RecvPlayerRespawn)
                {
                    try
                    {
                        var rp = param as CFW.Framework.EventParam_PlayerRespawn;
                        var pm2 = CFW.InGame.Player.PlayerManager.Instance;
                        var my2 = (pm2 != null) ? pm2.MyPlayer : null;
                        if (rp != null && my2 != null && my2.Data != null && rp.RespawnPlayerID == my2.Data.PlayerID)
                        {
                            int cur = my2.Data.Inventory.CurrentBag;
                            if (cur != 0 && rp.SelectBagIndex != cur)
                            {
                                if (_bagKeepLog++ < 8)
                                    Debug.Log("[CFZ-Offline][背包] 复活: 保持背包 " + (cur + 1) +
                                              " (游戏原事件想重置回背包 " + (rp.SelectBagIndex + 1) + ")");
                                rp.SelectBagIndex = cur;
                            }
                        }
                    }
                    catch { }
                }
                if (!_replayingRespawn
                    && gameEvent == CFW.Framework.GameEvent.Network_RecvPlayerRespawn
                    && param != null && _respParams.Count < 16)
                {
                    _respParams.Add(param);   // 捕获, 稍后可能重放
                }
                if (gameEvent == CFW.Framework.GameEvent.Loading_Complete)
                {
                    _loadingCompleteAt = Time.realtimeSinceStartup;
                    Debug.Log("[CFZ-Offline] ◆ 捕获 Loading_Complete, 开始 3 秒兜底计时");
                }
                string n = gameEvent.ToString();
#if DEBUG
                if (IsWatchedEvent(n)) Debug.Log("[CFZ-Offline] ◆ 派发 " + n);
#endif

                // ★栈探针已移除★ (2026-10-09): 已定位"弹窗开关循环"根因 (游戏键位表同帧 Hide + 旁路 Show)。
            }
            catch { }
            return true;
        }

        // ==================== QV/PV 模型路径探针 ====================

        static float _loadingCompleteAt = -1f;
        static bool _sentLoadingEnd = false;

        // 捕获 PlayerRespawn 事件参数, 供"玩家被留在 y=-1000"时重放
        static readonly System.Collections.Generic.List<CFW.Framework.IGameEventParam> _respParams =
            new System.Collections.Generic.List<CFW.Framework.IGameEventParam>();
        static bool _replayingRespawn = false;
        static bool _triedRespawn = false;

        public static void GetResPathPrefix(string parentFolderPath, string assetName)
        {
            try
            {
                Debug.Log("[CFZ-Offline] ◆◆ GetResourcePathCheckRegionFolder('" + parentFolderPath + "', '" + assetName + "')");
            }
            catch { }
        }

        public static void GetResPathPostfix(ref string __result)
        {
            try
            {
                bool ok = Resources.Load(__result) != null;
                Debug.Log("[CFZ-Offline] ◆◆   → 路径='" + __result + "'  Resources.Load=" + (ok ? "OK" : "NULL"));
                if (ok) return;
                string leaf = System.IO.Path.GetFileName(__result);
                string[] tries =
                {
                    __result.Replace("/Default/", "/SEA/"),
                    __result.Replace("/Default/", "/CHN/"),
                    __result.Replace("/Default/", "/DEV/"),
                    "Prefabs/Characters/QV/Character/" + leaf,
                    "Prefabs/Characters/QV/" + leaf,
                    "Prefabs/Characters/" + leaf,
                    "Prefabs/Characters/QV/Character/Default/" + leaf,
                };
                for (int i = 0; i < tries.Length; i++)
                {
                    if (tries[i] == __result) continue;
                    bool o2 = Resources.Load(tries[i]) != null;
                    Debug.Log("[CFZ-Offline] ◆◆   试 '" + tries[i] + "' = " + (o2 ? "★★★ OK ★★★" : "NULL"));
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] GetResPathPostfix 异常: " + e.Message); }
        }

        // AI 教程回合开始: 第62行 (GetQVModel().qvAnimation...) 在模型未就绪时 NRE
        // → 跳过该行, 但完整复刻 base.OnRoundStarting() + 输入解锁 (否则动不了)
        public static bool AIRoundStartingPrefix(object __instance)
        {
            try
            {
                try { } catch { }                                             // ★换图已移除★ 原地图自检已停用
                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                object mp = pm != null ? Member(pm, "MyPlayer") : null;
                object model = mp != null ? Member(mp, "Model") : null;
                object qvReady = model != null ? Member(model, "IsCreateQVModelComplete") : null;
                if (qvReady is bool && (bool)qvReady) return true;   // 模型正常 → 走原逻辑

                Debug.LogWarning("[CFZ-Offline][护盾] OnRoundStarting: QV模型未就绪(" +
                                 (model == null ? "Model=null" : "IsCreateQVModelComplete=False") +
                                 ") → 复刻原逻辑, 跳过第62行动画设置");

                // --- 1) 复刻 base.OnRoundStarting() ---
                var ws = typeof(InGameModeBase).GetField("WorldState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (ws != null)
                {
                    object v = Enum.Parse(ws.FieldType, "ROUND_STARTING");
                    ws.SetValue(null, v);
                    Debug.Log("[CFZ-Offline][护盾]   WorldState = " + v);
                }
                else Debug.LogWarning("[CFZ-Offline][护盾]   WorldState 字段未找到");

                object rt = Member(__instance, "roundTimer");
                if (rt != null)
                {
                    var m = rt.GetType().GetMethod("StartTimer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null)
                    {
                        var ps = m.GetParameters();
                        m.Invoke(rt, ps.Length == 1 ? new object[] { true } : null);
                        Debug.Log("[CFZ-Offline][护盾]   roundTimer.StartTimer 已调用 (参数数=" + ps.Length + ")");
                    }
                    else Debug.LogWarning("[CFZ-Offline][护盾]   StartTimer 未找到");
                }
                else Debug.LogWarning("[CFZ-Offline][护盾]   roundTimer 未找到");

                // --- 2) 解锁输入 (原第64-65行) ★ 这是"动不了"的关键 ---
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SetMovingEnable, null);
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SetEnable, null);
                Debug.Log("[CFZ-Offline][护盾]   已派发 Input_SetMovingEnable + Input_SetEnable");

                // --- 3) SendEventEx(InGameUI_SetLockWeaponBag, true) (原第66行) ---
                try
                {
                    var sx = typeof(InGameModeBase).GetMethod("SendEventEx", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sx != null)
                    {
                        var ps = sx.GetParameters();
                        if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))
                            sx.Invoke(__instance, new object[] { CFW.Framework.GameEvent.InGameUI_SetLockWeaponBag, true });
                    }
                }
                catch { }

                // --- 4) Trigger.SetColideRadius(1f) (原第67-68行) ---
                object trig = mp != null ? Member(mp, "Trigger") : null;
                if (trig != null)
                {
                    var tm = trig.GetType().GetMethod("SetColideRadius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (tm != null) { tm.Invoke(trig, new object[] { 1f }); Debug.Log("[CFZ-Offline][护盾]   Trigger.SetColideRadius(1f) 已调用"); }
                }
                return false;
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][护盾] 异常: " + e); return false; }
        }

        // ★解锁武器袋★ 教学模式 OnRoundStarting:66 会把武器袋锁死 (教学模式按设计不让换枪)。
        //   离线打 bot 没这个必要 → 回合开始后立刻补发 SetLockWeaponBag=false (与 InGameMode_Nano:388
        //   的官方解锁写法完全一致), B 键就能呼出背包。
        //   注意: 游戏自己的规则"离复活点 20m 内才能换背包"(bagEnableDistance) 保持不动 —— 这是 CF 传统。
        public static void AIRoundStartingPostfix()
        {
            try
            {
                CFW.Framework.GameEventHandler.DummySendEventEx(CFW.Framework.GameEvent.InGameUI_SetLockWeaponBag, false);
                Debug.Log("[CFZ-Offline][武器袋] 已解锁 (教学模式默认锁死)");

                // ★灵敏度 (ini [按键] 段)★ PlayerRotation.OnMouseMove:301 开镜分支:
                //   mouseX = mouseX / num * (Scope.Value / 100f) * 2f
                //   离线没载入用户设置 → Scope.Value 恒 0 → 开镜后鼠标增量 ×0 = 完全转不动。
                //   开镜灵敏度: ini 写 1~100 → 每回合强制设入; 留空 → 只在为 0 时补中性 50。
                //   鼠标灵敏度: 平时手感 (0.127 + 0.5×值/100), 留空不动当前值。
                try
                {
                    string sRaw = ModConfig.SV(ModConfig.Sec, "",
                        new string[] { "开镜灵敏度", "开镜", "Scope", "ScopeSensitivity" });
                    int sVal;
                    if (TryParseSens(sRaw, out sVal))
                    {
                        if (Common.System.Option.Option.Mouse.FPS.Scope.Value != sVal)
                        {
                            Common.System.Option.Option.Mouse.FPS.Scope.Value = sVal;
                            Debug.Log("[CFZ-Offline][开镜] 开镜灵敏度 = " + sVal + "% (ini: '" + sRaw.Trim() + "')");
                        }
                    }
                    else if (Common.System.Option.Option.Mouse.FPS.Scope.Value == 0)
                    {
                        Common.System.Option.Option.Mouse.FPS.Scope.Value = 50;
                        Debug.Log("[CFZ-Offline][开镜] 开镜灵敏度选项为 0 (离线没载入设置) → 已补成 50 (中性; ini 可覆盖)");
                    }

                    string nRaw = ModConfig.SV(ModConfig.Sec, "",
                        new string[] { "鼠标灵敏度", "普通灵敏度", "Normal", "MouseSensitivity" });
                    int nVal;
                    if (TryParseSens(nRaw, out nVal))
                    {
                        if (Common.System.Option.Option.Mouse.FPS.Normal.Value != nVal)
                        {
                            Common.System.Option.Option.Mouse.FPS.Normal.Value = nVal;
                            Debug.Log("[CFZ-Offline][开镜] 鼠标灵敏度 = " + nVal + "% (ini: '" + nRaw.Trim() + "')");
                        }
                    }
                }
                catch (Exception e2) { Debug.LogWarning("[CFZ-Offline][开镜] 灵敏度修复失败: " + e2.Message); }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][武器袋] 解锁失败: " + e.Message); }
        }

        // ★B 键随时呼出背包★ 在 OnShow_SelectPopup 判 IsEnable() 之前把袋按钮点亮:
        //   游戏两道限制 ①离复活点 20m(bagEnableDistance) ②开火/受击/死亡/丢枪战斗锁 全部绕开。
        //   SetEnable 内部还有一道 bLock 检查(教学模式锁, 已在回合开始时解锁), 不用重复处理。
        // ★灵敏度解析★ 只认 1~100 的整数 (写小数/其他文本 = 忽略, 走兜底逻辑)
        static bool TryParseSens(string raw, out int pct)
        {
            pct = 0;
            return int.TryParse((raw ?? "").Trim(), out pct) && pct >= 1 && pct <= 100;
        }

        public static void WeaponBagShowPrefix(CFW.UI.InGameUI.InGameUI_SelectWeaponBag __instance)
        {
            try { __instance.SetEnable(true); }
            catch { }
        }

        // ★开镜输入探针 (SetZoom/OnMouseMove/_UpdateMouse/三重门) 已移除★ (2026-10-09)
        //   完成了使命: 定位到根因 = Option.Mouse.FPS.Scope.Value 离线恒 0 → 开镜增量 ×0。
        //   修复在 AIRoundStartingPostfix (开镜灵敏度补 50 / 按 ini 覆盖)。

        // ==================== ★武器袋 UI 补初始化★ (B 键呼出背包) ====================
        //  链路: B → InGameUI_ShowSelectWeaponBag → ShowSelectWeaponBag → OnShow_SelectPopup
        //        → 前提 IsEnable() = 袋按钮 IsShow。按钮要亮, 得先有人把背包数据灌进 UI:
        //        PlayerManager._UpdateInGameUI_OnRespawn(:1533) → _UpdateInGameUI_WeaponBag(:1547)
        //        → 发 InGameUI_SetWeaponBag → SetWeaponBag(...) + OnRespawn(respawnPos)
        //        → isCheckRespawnDistance=true → 之后每帧 CheckIsEnable: 离复活点 20m 内 → 按钮亮。
        //  教学模式(离线)没人发这份数据 → 按钮永远不亮 → 即使锁已解, B 也无反应。
        //  这里替游戏补调一次 _UpdateInGameUI_WeaponBag(我, 我的位置), 并把面板状态打进日志。
        static int _bagUiFrame = 0;
        static int _bagUiTried = 0;
        static bool _bagUiArmed = false;   // 面板成功武装过一次 (此后 icr=false 就是开火/受击禁用的)

        static void BagUiFix(object mp)
        {
            try
            {
                // --- 1) 取 SelectWeaponBag 面板, 看初始化状态 ---
                object panel = null;
                string st = "";
                try
                {
                    var mgr = CFW.UI.InGameUI.InGameUI.Manager;
                    if (mgr != null)
                    {
                        foreach (var m in mgr.GetType().GetMethods())
                            if (m.Name == "GetPanel" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                            {
                                var gpi = m.MakeGenericMethod(typeof(CFW.UI.InGameUI.InGameUI_SelectWeaponBag));
                                var pt = gpi.GetParameters()[0].ParameterType;
                                panel = gpi.Invoke(mgr, new object[] { Enum.Parse(pt, "SelectWeaponBag") });
                                break;
                            }
                    }
                }
                catch (Exception e) { st = "(取面板异常:" + e.Message + ")"; }

                if (panel == null)
                {
                    if (_bagUiTried++ < 6)
                        Debug.LogWarning("[CFZ-Offline][武器袋] SelectWeaponBag 面板不存在 " + st +
                                         " → 这套教学 UI 没带武器袋面板 (那 B 键呼不出就要换面板了, 反馈给我)");
                    return;
                }

                object icr = Member(panel, "isCheckRespawnDistance");
                if (icr is bool && (bool)icr)
                {
                    if (!_bagUiArmed && _bagUiTried++ < 6)
                        Debug.LogWarning("[CFZ-Offline][武器袋] UI 已初始化 → B 键随时可呼出 (无 20m/战斗锁限制)");
                    _bagUiArmed = true;
                    return;
                }

                if (mp == null) return;
                var comp = mp as UnityEngine.Component;
                if (comp == null) return;
                var pp = comp.transform.position;
                if (pp.y < -900f) return;                       // 还在局外占位坐标 (0,-1000,0), 等真进图

                // --- 2) 从没武装过 → 替游戏补调灌数据函数 (与真实复活走的同一条路) ---
                //   (灌完背包列表/图标; 之后开火/离复活点把按钮关掉也无所谓 ——
                //    WeaponBagShowPrefix 会在按 B 那一刻重新点亮按钮)
                object data = Member(mp, "Data");
                object pid = data != null ? Member(data, "PlayerID") : null;
                if (pid == null) return;

                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                var w = pm != null ? pm.GetType().GetMethod("_UpdateInGameUI_WeaponBag",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (w == null)
                {
                    if (_bagUiTried++ < 6) Debug.LogWarning("[CFZ-Offline][武器袋] 找不到 PlayerManager._UpdateInGameUI_WeaponBag");
                    return;
                }
                w.Invoke(pm, new object[] { pid, pp });
                if (_bagUiTried++ < 6)
                    Debug.LogWarning("[CFZ-Offline][武器袋] 教学 UI 没灌过背包数据 → 已补调 _UpdateInGameUI_WeaponBag(" +
                                     pid + ", " + pp.ToString("F0") + ") 第 " + _bagUiTried + " 次");
            }
            catch (Exception e) { if (_bagUiTried++ < 6) Debug.LogWarning("[CFZ-Offline][武器袋] 补初始化异常: " + e.Message); }
        }

        // ==================== 进图后: 位置/相机/输入 观测 ====================

        static float _lastWatch2 = -100f;
        static int _mapWatchCount = 0;

        // ==================== ★输入闸门★ CFW.GameSystem.InputManager ====================
        //
        // Update() 第376行: if (!isLoadingEnd || _currInputType == InputType.None) return;
        // 两道闸门任意为真 → 一个按键都不读。_currInputType 只由 Input_SelectInput_* 设置。

        static int _inputKickCount = 0;

        static void KickInputManager()
        {
            try
            {
                if (_inputKickCount >= 8) return;
                var imT = typeof(CFW.GameSystem.InputManager);
                var im = UnityEngine.Object.FindObjectOfType(imT);
                if (im == null)
                {
                    Debug.LogWarning("[CFZ-Offline][输入] ★ CFW.GameSystem.InputManager 实例不存在!");
                    return;
                }
                _inputKickCount++;

                object isLoadingEnd = Member(im, "isLoadingEnd");
                object curr = Member(im, "_currInputType");
                object move = Member(im, "isEnableMove");
                object mouse = Member(im, "isEnableMouse");
                object locked = Member(im, "CursorLocked");
                var dic = Member(im, "dicTable") as System.Array;
                int nonNull = 0;
                if (dic != null) for (int i = 0; i < dic.Length; i++) if (dic.GetValue(i) != null) nonNull++;

                Debug.Log("[CFZ-Offline][输入] #" + _inputKickCount + " isLoadingEnd=" + isLoadingEnd +
                          " _currInputType=" + curr + " isEnableMove=" + move + " isEnableMouse=" + mouse +
                          " CursorLocked=" + locked + " dicTable非空=" + nonNull + "/" + (dic != null ? dic.Length.ToString() : "?"));

                // 闸门 1: isLoadingEnd (由 GameEvent.Loading_End 置 true)
                if (isLoadingEnd is bool && !(bool)isLoadingEnd)
                {
                    Debug.LogWarning("[CFZ-Offline][输入] isLoadingEnd=False → 补发 Loading_End");
                    CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Loading_End, null);
                }

                // 闸门 2: _currInputType == None (由 Input_SelectInput_* 设置)
                string cs = curr != null ? curr.ToString() : "?";
                if (cs == "None")
                {
                    if (nonNull > 1)
                    {
                        Debug.LogWarning("[CFZ-Offline][输入] _currInputType=None → 补发 Input_Init + Input_SelectInput_InGame");
                        CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_Init, null);
                        CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SelectInput_InGame, null);
                    }
                    else
                    {
                        Debug.LogError("[CFZ-Offline][输入] dicTable 为空 (InputTableText 未赋值) → 无法补发 SelectInput");
                    }
                }

                // 闸门 3: isEnableMove / isEnableMouse / 鼠标锁定
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SetMovingEnable, null);
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SetMouseEnable, null);
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_SetEnable, null);
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_LockCursor, null);

                Debug.Log("[CFZ-Offline][输入] 补发后 _currInputType=" + Member(im, "_currInputType") +
                          " isEnableMove=" + Member(im, "isEnableMove") + " isLoadingEnd=" + Member(im, "isLoadingEnd"));
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][输入] 异常: " + e.Message); }
        }

        // ==================== ★按键表修复★ Option.Key.FPS 的 Value 没被灌入 ====================
        //
        // Option.Load() (Option.cs 第94-103行):
        //   LoadDefault();                              // 只填 KeySetting.Primary.Key.Default
        //   if (!File.Exists(Option.xml)) { Save(); return; }   // ★ 直接返回 → Value 仍是 KeyCode.None!
        // KeyMap.GetKey() 读的是 Primary.Key.Value (第83行) → 返回 None → Input.GetKey(None) 恒 false
        // → _UpdateMove 里 moveDir 永远是 0 → 一个移动事件都不派发 → "动不了"
        // 修复: Option.Key.SetDefault() → KeyMap.SetDefault() → OptionValue.SetDefault(): Value = Default

        static int _inputUpdateCount = 0;
        static bool _keymapChecked = false;
        static bool _keymapBroken = false;

        static void CheckAndRepairKeyMap()
        {
            try
            {
                var km = Common.System.Option.Option.Key.FPS;
                var dict = Member(km, "map") as System.Collections.IDictionary;
                KeyCode kFront = km.GetKey(Common.System.Option.GameKey.FPS_Move_Front);
                KeyCode kBack = km.GetKey(Common.System.Option.GameKey.FPS_Move_Back);
                KeyCode kLeft = km.GetKey(Common.System.Option.GameKey.FPS_Move_Left);
                KeyCode kRight = km.GetKey(Common.System.Option.GameKey.FPS_Move_Right);
                object ta = UnityEngine.Resources.Load<UnityEngine.TextAsset>("Option/DefaultOption");
                Debug.LogWarning("[CFZ-Offline][按键表] map.Count=" + (dict != null ? dict.Count.ToString() : "null") +
                    " 含FPS_Move_Front=" + (dict != null && dict.Contains("FPS_Move_Front")) +
                    " | GetKey W/S/A/D=" + kFront + "/" + kBack + "/" + kLeft + "/" + kRight +
                    " | GetDefaultKey(Front)=" + km.GetDefaultKey(Common.System.Option.GameKey.FPS_Move_Front) +
                    " | Resources[Option/DefaultOption]=" + (ta == null ? "★NULL★" : "OK"));

                if (kFront != KeyCode.None && kBack != KeyCode.None && kLeft != KeyCode.None && kRight != KeyCode.None)
                {
                    Debug.Log("[CFZ-Offline][按键表] 按键正常, 无需修复");
                    return;
                }

                Debug.LogWarning("[CFZ-Offline][按键表] 按键为空 → 调用 Option.Key.SetDefault() (Value = Default)");
                Common.System.Option.Option.Key.SetDefault();
                KeyCode after = km.GetKey(Common.System.Option.GameKey.FPS_Move_Front);
                Debug.LogWarning("[CFZ-Offline][按键表] SetDefault 后 GetKey(Front)=" + after);

                if (after == KeyCode.None)
                {
                    _keymapBroken = true;
                    Debug.LogError("[CFZ-Offline][按键表] 仍为 None → 启用原生 WASD 旁路 (直接派发 Input_TryMove)");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][按键表] 异常: " + e); }
        }

        // 原生 WASD 旁路: 完全绕过 Option.Key.FPS, 复用 InputManager 自己的 moveKeyParam
        static int _ourMoveDir = 0;

        static void KeymapBypassMove()
        {
            try
            {
                var im = UnityEngine.Object.FindObjectOfType<CFW.GameSystem.InputManager>();
                if (im == null) return;
                object en = Member(im, "isEnableMove");
                if (en is bool && !(bool)en) return;                       // 游戏自己禁用移动时不插手
                var p = Member(im, "moveKeyParam") as CFW.Framework.EventParam_PlayerMove;
                if (p == null) return;

                int d = 0;
                if (ModConfig.Held(ModConfig.K("前进", "MoveFront"))) d |= 1;
                if (ModConfig.Held(ModConfig.K("后退", "MoveBack"))) d |= 2;
                if (ModConfig.Held(ModConfig.K("左移", "MoveLeft"))) d |= 8;
                if (ModConfig.Held(ModConfig.K("右移", "MoveRight"))) d |= 4;

                if (d == _ourMoveDir && (d == 0 || _inputUpdateCount % 30 != 0)) return;
                _ourMoveDir = d;

                p.moveDir = d;
                p.isCrouch = ModConfig.Held(ModConfig.K("蹲下", "Duck"));
                p.isWalk = ModConfig.Held(ModConfig.K("静步", "Walk"));

                if (d != 0)
                {
                    CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_TryMove, p);
                    Debug.Log("[CFZ-Offline][旁路] moveDir=" + d + " → 派发 Input_TryMove");
                }
                else
                {
                    CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Input_TryDirectionIdle, p);
                    Debug.Log("[CFZ-Offline][旁路] moveDir=0 → 派发 Input_TryDirectionIdle");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][旁路] 异常: " + e.Message); }
        }

        // ==================== ★全按钮旁路★ 键位表整张为空时接管所有按钮 ====================
        //
        // 实测 (日志 11813-11825):
        //   [按键表] map.Count=0 含FPS_Move_Front=False | GetKey W/S/A/D=None/None/None/None
        //            | GetDefaultKey(Front)=None | Resources[Option/DefaultOption]=OK
        //   [按键表] SetDefault 后 GetKey(Front)=None        ← ★ 连 Default 都是 None, 修不回来
        //
        // 机制: InputTable.UpdateTable() 唯一派发口 InputKey.CheckInput():
        //           if (GameKey != GameKey.None) Key = Option.Key.FPS.GetKey(GameKey);   ← ★ 全 None
        //           if (Key != KeyCode.None) { ...读 Input.GetKey... }                    ← ★ 整段跳过
        //           IsRaiseEvent = (currState == KeyState);                               ← ★ 永远 false
        //   → 开火/跳跃/换武器/换弹/下蹲/走路 一个事件都发不出来。
        //   而鼠标轴(_UpdateMouse)与移动(_UpdateMove)是**直接读 Input** 的 → 只有它们正常
        //   (移动能走, 是因为已给它写了 KeymapBypassMove)
        //
        // 修复: 自己读物理键, 派发与 InputTable 完全相同的 GameEvent (参数一律 null,
        //       与 InputTable.UpdateTable 的 SendEvent(ev, null) 一致)。
        //   ★只在 _keymapBroken (键位表确实是空的) 时启用 → 绝不与游戏原生路径重复派发。
        //   ★分帧语义与表一致: 按下帧 Start / 持续帧 Fire / 抬起帧 End。

        static bool _bpFire = false, _bpFireSub = false, _bpDuck = false, _bpWalk = false;
        static bool _bpScore = false, _bpMap = false;
        static int _bpLogged = 0;
        static int _kbbFrame = -1;                                  // 同帧去重 (多 InputManager 实例)
        static bool _bagOpenPrev = false;                           // 上一帧末武器袋弹窗是否开着
        static int _bagChangeLog = 0;                               // 换袋日志限量
        static int _bagKeepLog = 0;                                 // 复活保持背包日志限量

        // 读武器袋弹窗当前是否开着 (面板不存在 → false)
        static bool BagDialogOpen()
        {
            try
            {
                var mgr = CFW.UI.InGameUI.InGameUI.Manager;
                if (mgr == null) return false;
                foreach (var m in mgr.GetType().GetMethods())
                    if (m.Name == "GetPanel" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    {
                        var gpi = m.MakeGenericMethod(typeof(CFW.UI.InGameUI.InGameUI_SelectWeaponBag));
                        var pt = gpi.GetParameters()[0].ParameterType;
                        var p = gpi.Invoke(mgr, new object[] { Enum.Parse(pt, "SelectWeaponBag") })
                                as CFW.UI.InGameUI.InGameUI_SelectWeaponBag;
                        return p != null && p.IsOpenDialog();
                    }
            }
            catch { }
            return false;
        }

        static void SendBtn(CFW.Framework.GameEvent ev)
        {
            try { CFW.Framework.GameEventHandler.DummySendEvent(ev, null); } catch { }
        }

        // 单击型: 命中就派发 + 记一条首遇日志
        static void BtnDown(bool cond, CFW.Framework.GameEvent ev, string label)
        {
            if (!cond) return;
            SendBtn(ev);
            if (_bpLogged < 16) { _bpLogged++; Debug.LogWarning("[CFZ-Offline][旁路][按键] 已接管: " + label); }
        }

        // 按住型: 与 InputTable 的分帧语义一致 (按下帧 Start / 抬起帧 End)
        static void BtnHold(ref bool state, KeyCode[] kk, CFW.Framework.GameEvent onDown,
                            CFW.Framework.GameEvent onUp, string label)
        {
            bool now = ModConfig.Held(kk);
            if (now == state) return;
            state = now;
            SendBtn(now ? onDown : onUp);
            if (_bpLogged < 16) { _bpLogged++; Debug.LogWarning("[CFZ-Offline][旁路][按键] 已接管: " + label + (now ? " (按下)" : " (松开)")); }
        }

        static void KeymapBypassButtons()
        {
            try
            {
                // ★同帧去重★ 场景里有多个 InputManager 实例 → Update 各跑一次 → 本函数一帧被调多次。
                //   同一次按键会被派发两遍: WeaponBag 态发完 Hide, 同帧第二次调用时输入已切回 InGame,
                //   GetKeyDown 在同帧仍是 true → 又发 Show → 弹窗关了立刻重开 ("按B关不掉"的真因)。
                int fc = UnityEngine.Time.frameCount;
                if (fc == _kbbFrame) return;
                _kbbFrame = fc;

                // ★弹窗开/关打架★: 游戏键位表在 InputManager.Update 里关掉弹窗后, 输入态同帧切回 InGame,
                //   到我们 postfix 时已经看不出"刚关过"。记下上一帧末弹窗是否开着 → 那次按键是"关闭", 别再发 Show。
                bool bagWasOpen = _bagOpenPrev;
                _bagOpenPrev = BagDialogOpen();

                var im = UnityEngine.Object.FindObjectOfType<CFW.GameSystem.InputManager>();
                if (im == null) return;
                // 只在真正的战斗输入表下接管 (InGame / Tutorial); 菜单/聊天/观战不插手
                object cur = Member(im, "_currInputType");
                string cs = cur != null ? cur.ToString() : "?";

                // ★武器袋弹窗开着时 (输入态=WeaponBag): B / ESC 关闭弹窗 (兜底)。
                //   栈探针证实: WeaponBag 态的游戏键位表其实有 B 绑定 —— InputManager.Update 里的
                //   UpdateTable() 会自己发 InGameUI_HideSelectWeaponBag 关弹窗。但同帧我们的 InGame 分支
                //   又看到 B 按下沿(此时输入态已被切回 InGame) → 发 Show 重开 → "关了立刻开"死循环。
                //   主修复在下面 InGame 分支(跳过"上一帧弹窗还开着"的 Show); 这里只做兜底。
                if (cs == "WeaponBag")
                {
                    if (ModConfig.Down(ModConfig.K("装备背包", "Bag")) || ModConfig.Down(ModConfig.K("菜单", "Menu")))
                        SendBtn(CFW.Framework.GameEvent.InGameUI_HideSelectWeaponBag);
                    return;
                }

                if (cs != "InGame" && cs != "Tutorial") return;

                // ================= 按住型 =================
                // 开火 (连发语义: 按下 Start / 持续 Fire / 抬起 End)
                bool fire = ModConfig.Held(ModConfig.K("开火", "Fire"));
                if (fire != _bpFire)
                {
                    _bpFire = fire;
                    SendBtn(fire ? CFW.Framework.GameEvent.Input_TryFireStart
                                 : CFW.Framework.GameEvent.Input_TryFireEnd);
                    if (fire && _bpLogged < 16) { _bpLogged++; Debug.LogWarning("[CFZ-Offline][旁路][按键] 已接管: 开火 (按下)"); }
                }
                else if (fire) SendBtn(CFW.Framework.GameEvent.Input_TryFire);

                // 副开火 / 开镜
                bool sub = ModConfig.Held(ModConfig.K("副开火", "SubFire"));
                if (sub != _bpFireSub)
                {
                    _bpFireSub = sub;
                    SendBtn(sub ? CFW.Framework.GameEvent.Input_TryFireSubWeaponStart
                                : CFW.Framework.GameEvent.Input_TryFireSubWeaponEnd);
                }
                else if (sub) SendBtn(CFW.Framework.GameEvent.Input_TryFireSubWeapon);

                BtnHold(ref _bpDuck, ModConfig.K("蹲下", "Duck"),
                        CFW.Framework.GameEvent.Input_TryDuckStart, CFW.Framework.GameEvent.Input_TryDuckEnd, "蹲下");
                BtnHold(ref _bpWalk, ModConfig.K("静步", "Walk"),
                        CFW.Framework.GameEvent.Input_TryWalkStart, CFW.Framework.GameEvent.Input_TryWalkEnd, "静步");
                BtnHold(ref _bpScore, ModConfig.K("记分板", "ScoreBoard"),
                        CFW.Framework.GameEvent.InGameUI_ShowScoreBoard, CFW.Framework.GameEvent.InGameUI_HideScoreBoard, "记分板");
                BtnHold(ref _bpMap, ModConfig.K("战术地图", "TacticalMap"),
                        CFW.Framework.GameEvent.InGameUI_ShowMap, CFW.Framework.GameEvent.InGameUI_HideMap, "战术地图");

                // ================= 单击型 =================
                BtnDown(ModConfig.Down(ModConfig.K("跳跃", "Jump")), CFW.Framework.GameEvent.Input_TryJump, "跳跃");
                BtnDown(ModConfig.Down(ModConfig.K("换弹", "Reload")), CFW.Framework.GameEvent.Input_TryReload, "换弹");

                BtnDown(ModConfig.Down(ModConfig.K("主武器", "Slot1")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot1, "主武器");
                BtnDown(ModConfig.Down(ModConfig.K("副武器", "Slot2")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot2, "副武器");
                BtnDown(ModConfig.Down(ModConfig.K("近战", "Slot3")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot3, "近战");
                BtnDown(ModConfig.Down(ModConfig.K("投掷", "Slot4")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot4, "投掷");
                BtnDown(ModConfig.Down(ModConfig.K("功能", "Slot5")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot5, "功能武器");
                BtnDown(ModConfig.Down(ModConfig.K("第六槽", "Slot6")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlot6, "第六槽");
                BtnDown(ModConfig.Down(ModConfig.K("上一把", "PrevWeapon")), CFW.Framework.GameEvent.Input_TryChangeWeaponPrev, "上一把武器");
                BtnDown(ModConfig.Down(ModConfig.K("下一把", "NextWeapon")), CFW.Framework.GameEvent.Input_TryChangeWeaponSlotLower, "下一把武器");

                // ★开/关打架主修复★ 游戏表同帧刚用这次 B 按下发了 Hide(关闭),
                //   "上一帧弹窗还开着"时跳过 Show, 让关闭生效 (下一帧起恢复正常打开)
                if (!bagWasOpen)
                    BtnDown(ModConfig.Down(ModConfig.K("装备背包", "Bag")), CFW.Framework.GameEvent.InGameUI_ShowSelectWeaponBag, "装备背包");
                BtnDown(ModConfig.Down(ModConfig.K("聊天", "Chat")), CFW.Framework.GameEvent.Input_ShowChatting, "聊天");
                BtnDown(ModConfig.Down(ModConfig.K("无线电1", "Radio1")), CFW.Framework.GameEvent.Input_ShowRadioMessage0, "无线电1");
                BtnDown(ModConfig.Down(ModConfig.K("无线电2", "Radio2")), CFW.Framework.GameEvent.Input_ShowRadioMessage1, "无线电2");
                BtnDown(ModConfig.Down(ModConfig.K("无线电3", "Radio3")), CFW.Framework.GameEvent.Input_ShowRadioMessage2, "无线电3");
                BtnDown(ModConfig.Down(ModConfig.K("丢弃武器", "DropWeapon")), CFW.Framework.GameEvent.Input_TryDropWeapon, "丢弃武器");
                BtnDown(ModConfig.Down(ModConfig.K("武器详情", "WeaponDetail")), CFW.Framework.GameEvent.Input_ObserveWeapon, "武器详情");

                // ---- ★菜单★ ----
                // 游戏原生路径: InputTable 里 ESC 绑的 GameEvent = InGameUI_ShowMenu
                //   处理者1: PlayerInputEvent.OnEvent_ShowMenu (PlayerInputEvent.cs:623) → player.Shot.OnReset(isFocusReturn:true)
                //   处理者2: InGameUI.OnEvent_ShowMenu      (InGameUI.cs:1046)          → inGameUI.ShowMenu()
                //            → InGameUI_Base.ShowMenu (InGameUI_Base.cs:1327) → InGameUI.Manager.OnShow(PanelType.Menu)
                //   教学模式的面板就是 InGameUI_TutorialEscape (InGameUI_Tutorial.cs:22,51 —— 日志里该 Prefab 已加载成功)
                if (ModConfig.Down(ModConfig.K("菜单", "Menu")))
                {
                    object before = Member(im, "CursorLocked");
                    SendBtn(CFW.Framework.GameEvent.InGameUI_ShowMenu);
                    object after = Member(im, "CursorLocked");
                    Debug.LogWarning("[CFZ-Offline][旁路][按键] 已接管: 菜单 → InGameUI_ShowMenu | CursorLocked " +
                                     before + " → " + after +
                                     (object.Equals(before, after) ? " (菜单没接管输入? 若界面没弹出请反馈)" : " ✓ 菜单已接管输入"));
                }
            }
            catch { }
        }

        // 挂在 CFW.GameSystem.InputManager.Update 之后, 每帧执行
        public static void InputManagerUpdatePostfix()
        {
            try
            {
                _inputUpdateCount++;
                if (!_keymapChecked && _inputUpdateCount > 90)
                {
                    _keymapChecked = true;
                    CheckAndRepairKeyMap();
                }
                CursorEnforce();                                  // ★ 光标: 游戏意图=锁定 时强制隐藏
                if (_keymapBroken) KeymapBypassMove();
                if (_keymapBroken) KeymapBypassButtons();         // ★ 全按钮旁路 (开火/跳跃/换武器/换弹/蹲/走/ESC)
                if (!_camAttachDone) EnsureCameraAttached();      // ★ 相机挂载 (补发 Camera_ChangeViewMode)
                EnsureRoundStart();                              // ★ 回合开始 (补发 Network_RecvRoundStart)
                EnsureRuntimeFixes();                            // ★ 视角/音频/相机抢占 巡检
                EnsureSoundFix();                                // ★ 声音: Option 音量全空被乘成 0 + Listener 补挂
                EnsureGraphicFix();                              // ★ 亮度: Option.Brightness=0 → ChangeCameraBright=0.5 → 暗屏
                //EnsureModelVisibility();                       // ★可见性矩阵★ 已停用 (2026-10-09 开镜排查):
                //   每 15 帧按死/活重申 PV(手/枪)/QV(身体) 显隐。测试它与开镜打架与否;
                //   若死亡后看不到第三人称身体, 反馈我改回"仅死亡时生效"。
            }
            catch { }
        }

        // ==================== ★模型可见性矩阵★ ====================
        //
        //  ★上一轮我把两个模型搞反了★ 正确分工 (全部有代码证据):
        //    PV = 第一人称"手 + 枪"      Player.OnCreate_PVHand (Player.cs:535) → Model.PV_Create_Hand
        //                                渲染在 "PV" 层(8)  ← FPVCamera.cullingMask=256 只看这一层
        //    QV = 第三人称"整具身体"      Model.QV_Create_Model (PlayerModel.cs:382) → 含 Pelvis/Spine/Neck 骨骼
        //                                渲染在 "QV" 层     ← 主相机 cullingMask=-1025 含这一层
        //    "Hidden" 层(10): 两台相机都不渲染 (主相机 mask 恰好排除 bit10) → 游戏通用的"藏东西"手段
        //
        //  游戏自己的两个开关 (由 SceneCameraNormal.SetTarget/Exit 调用):
        //    Player.cs:1931 OnCameraAttached(): IsPVPlayer=true;  OnShowPVModel(true)    ← 手/枪 → "PV"
        //                                                          Model.QV_OnShow(false) ← 身体 → "Hidden"
        //    Player.cs:1943 OnCameraDetached(): IsPVPlayer=false; OnShowPVModel(false); QV_HideAllWeapons(false);
        //                                                          ShowQVModel(true)      ← 身体 → "QV"
        //  底层实现全是改 layer (不动 activeSelf / Renderer.enabled):
        //    QVModel.cs:448 ShowModel(bool)  整棵 QV 子树 → "QV"/"Hidden" + ShowWeapon(当前武器)
        //    QVModel.cs:855 OnShow(bool)     QV|Default → Hidden ; Default|Hidden → QV (不碰武器)
        //    PVModel.cs:759 OnShow(bool)     Hand + 当前武器 子树 → "PV" / "Hidden"
        //
        //  ★死亡的原生链路★ AI_TutorialPlayerState_die.cs:34 → PlayerModel.OnDie (PlayerModel.cs:647)
        //      → QVModel.OnDie (QVModel.cs:741): HideAllWeapons(false) + qvAnimation.OnDie(...) 播"倒地"动画
        //    → 倒地动画作用在 **QV(身体)** 上 ⇒ 死亡时: 身体必须**可见**, 第一人称手/枪必须**藏起来**。
        //      而"相机离开我"(OnCameraDetached) 只在有 killcam / 观战时才触发, 离线教学里没有:
        //        活着 → 身体(QV)没被藏起来 (本应 ShowQVModel(false))  ← "存活却能看到第三人称"
        //        死亡 → 第一人称(PV)没被藏起来                        ← 用户最初反馈的问题
        //  修法: 不依赖那两条事件, 每 15 帧按状态把矩阵重申到位 (纯 layer 写入, 幂等, 零副作用),
        //        复活时再把第一人称手/枪要回来一次。

        static int _visFrame = 0;
        static bool _visDead = false;
        static int _visLog = 0;

        static void EnsureModelVisibility()
        {
            try
            {
                if (++_visFrame % 15 != 1) return;
                var pm = CFW.InGame.Player.PlayerManager.Instance;
                var mp = pm != null ? pm.MyPlayer : null;
                if (mp == null) return;

                bool dead = mp.IsDeadState;

                if (dead)
                {
                    // ---- 与游戏 OnCameraDetached (Player.cs:1943) 完全一致 ----
                    mp.OnShowPVModel(false);                                   // 第一人称 手/枪 → "Hidden"
                    if (mp.Model != null) mp.Model.QV_HideAllWeapons(false);    // 身体手里那把枪先藏
                    mp.ShowQVModel(true);                                      // 我的身体 → "QV" (倒地动画看得见)
                    if (!_visDead)
                    {
                        _visDead = true;
                        if (_visLog++ < 3)
                            Debug.LogWarning("[CFZ-Offline][可见性] 死亡 → 隐藏第一人称 PV(手/枪) + 显示身体 QV(倒地)" +
                                             " | IsPVPlayer=" + mp.IsPVPlayer + " " + LayerReport(mp));
                    }
                }
                else
                {
                    // ---- 与游戏 OnCameraAttached (Player.cs:1931) 一致 ----
                    bool back = _visDead;
                    if (back) mp.OnShowPVModel(true);            // 复活: 手/枪要回来 (只做一次, 免得打断开镜/观察武器)
                    if (mp.Model != null) mp.Model.QV_OnShow(false);  // 身体 → "Hidden" (每 15 帧重申, 抗复活后模型重建)
                    if (back)
                    {
                        _visDead = false;
                        if (_visLog++ < 6)
                            Debug.LogWarning("[CFZ-Offline][可见性] 复活 → 显示第一人称 PV(手/枪) + 隐藏身体 QV" +
                                             " | IsPVPlayer=" + mp.IsPVPlayer + " " + LayerReport(mp));
                    }
                }
            }
            catch { }
        }

        // 诊断: 打印 PV/QV 两棵子树当前的层号分布, 看矩阵有没有落地
        static string LayerReport(CFW.InGame.Player.Player p)
        {
            try
            {
                object mdl = Member(p, "Model");
                object pv = ModelOf(mdl, "GetPVModel");
                object qv = ModelOf(mdl, "GetQVModel");
                return "| 层号(PV=" + LayerMask.NameToLayer("PV") + " QV=" + LayerMask.NameToLayer("QV") +
                       " Hidden=" + LayerMask.NameToLayer("Hidden") + ") PV子树" + LayerList(pv) +
                       " QV子树" + LayerList(qv);
            }
            catch (Exception e) { return "| LayerReport 异常: " + e.Message; }
        }

        static object ModelOf(object playerModel, string getter)
        {
            if (playerModel == null) return null;
            try
            {
                var m = playerModel.GetType().GetMethod(getter, F2);
                if (m != null) return m.Invoke(playerModel, null);
            }
            catch { }
            return null;
        }

        static string LayerList(object model)
        {
            var c = model as UnityEngine.Component;
            if (c == null) return "=★null★";
            var rs = c.GetComponentsInChildren<Renderer>(true);
            var sb = new System.Text.StringBuilder("=").Append(rs.Length).Append("个[");
            for (int i = 0; i < rs.Length && i < 10; i++) sb.Append(rs[i].gameObject.layer).Append(' ');
            return sb.Append(']').ToString();
        }

        // ==================== ★模型修复★ 强制角色/武器走 AssetBundle ====================
        //
        // ResourceLoader.Load<T>() 第169-179行:
        //     if (_NeedPrefabResource(bundleName, assetName)) StartCoroutine(_LoadFromPrefabAsync<T>(job));  // ← Resources
        //     else                                            bundleLoader.Load<T>(...);                      // ← bundle
        //   _NeedPrefabResource = !bundleList.IsBundle(bundleName, assetName)                      (第141-144行)
        //   BundleList.IsBundle(string,string) => bundleChart.HasBundle(name, asset)
        //   AssetBundleChart.HasBundle 第197-204行:
        //     if (_dicBundleInfo.TryGetValue(bundleName.ToLower(), out var v))
        //         return Array.Exists(v.ResourcePath, p => p.Contains(assetName, ...));
        //   ★ bundle 的 ResourcePath = "Assets/Game Assets/_ResourcesToAssetBundle/Character/fps_m_swat.prefab"
        //   ★ 而传进来的 assetName    = "Prefabs/Characters/QV/Character/Default/FPS_M_SWAT_BL_INGAME_CHARACTER"
        //   → Contains = false → IsBundle = false → _NeedPrefabResource = true → Resources → asset=null
        //   → IsCreateQVModelComplete 永远 False → Model 不完整 → Shot 不创建
        //   → PlayerMoveFixedUpdate 的 `if (Shot != null)` 永远为假 → 坐标永远冻结 ("动不了")
        //
        // ★ Harmony 要点 ★: prefix 里读 __result 拿到的是默认值; 要让原方法不执行并沿用我们的值,
        //                   prefix 必须返回 false。

        static bool _bundleProbeLogged = false;

        static string LookupBundleName(string bundleName)
        {
            try
            {
                var rl = Common.System.ResourceLoader.Instance;
                if (rl == null) return null;
                object bl = Member(rl, "bundleLoader");
                if (bl == null) return null;
                object blist = Member(bl, "bundleList");
                if (blist == null) return null;
                var gi = blist.GetType().GetMethod("GetBundleInfo", F2);
                if (gi == null) return null;
                object info = gi.Invoke(blist, new object[] { bundleName });
                if (info == null) return null;
                string n = Member(info, "Name") as string;
                return string.IsNullOrEmpty(n) ? null : n;
            }
            catch { return null; }
        }

        static void LogBundleProbeOnce(string bundleName, string found)
        {
            if (_bundleProbeLogged) return;
            _bundleProbeLogged = true;
            try
            {
                var rl = Common.System.ResourceLoader.Instance;
                object bl = rl != null ? Member(rl, "bundleLoader") : null;
                object blist = bl != null ? Member(bl, "bundleList") : null;
                object chart = blist != null ? Member(blist, "bundleChart") : null;
                var dic = (chart != null ? Member(chart, "_dicBundleInfo") : null) as System.Collections.IDictionary;
                string root = UnityEngine.Application.dataPath + "/../Bundle";
                Debug.LogWarning("[CFZ-Offline][bundle] chart条目数=" + (dic != null ? dic.Count.ToString() : "?") +
                    " 含fps_m_swat=" + (dic != null && dic.Contains("fps_m_swat")) +
                    " | GetBundleInfo('" + bundleName + "').Name=" + (found ?? "★空★") +
                    " | 磁盘 Bundle/Character/fps_m_swat.unity3d=" + System.IO.File.Exists(root + "/Character/fps_m_swat.unity3d") +
                    " | Bundle/Weapon/fps_we_ak47.unity3d=" + System.IO.File.Exists(root + "/Weapon/fps_we_ak47.unity3d"));
                DumpSwatChart();
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][bundle] 探针异常: " + e.Message); }
        }

        // ★ chart 的键不是 bundle 名, 而是 ResourceName (被 ResourcePath[0] 剥前缀覆盖过)
        //   AssetBundleChart._CollectAssetBundleResouceNames: _dicBundleInfo.Add(ResourceName.ToLower(), info)
        //   → "Assets/Game Assets/_ResourcesToAssetBundle/Character/fps_m_swat.prefab"
        //   → 键 = "character/fps_m_swat"  (而游戏传 'FPS_M_SWAT' → 键 "fps_m_swat" → miss)
        static void DumpSwatChart()
        {
            try
            {
                var rl = Common.System.ResourceLoader.Instance;
                object bl = rl != null ? Member(rl, "bundleLoader") : null;
                object blist = bl != null ? Member(bl, "bundleList") : null;
                object chart = blist != null ? Member(blist, "bundleChart") : null;
                var dic = (chart != null ? Member(chart, "_dicBundleInfo") : null) as System.Collections.IDictionary;
                if (dic == null) { Debug.LogWarning("[CFZ-Offline][bundle] _dicBundleInfo 取不到"); return; }

                int n = 0;
                foreach (System.Collections.DictionaryEntry de in dic)
                {
                    object info = de.Value;
                    if (info == null) continue;
                    string key = de.Key as string;
                    string nm = Member(info, "Name") as string;
                    string rn = Member(info, "ResourceName") as string;
                    object bt = Member(info, "BundleType");
                    string rp = "";
                    var arr = Member(info, "ResourcePath") as string[];
                    if (arr != null && arr.Length > 0) rp = arr[0];
                    string all = (key ?? "") + "|" + (nm ?? "") + "|" + (rn ?? "") + "|" + rp;
                    if (all.IndexOf("swat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    if (n <= 25)
                    {
                        Debug.LogWarning("[CFZ-Offline][bundle-swat] key='" + key + "' Name='" + nm + "' ResName='" + rn +
                                         "' Type=" + (bt != null ? bt.ToString() : "?") + " Path0='" + rp + "'");
                    }
                }
                Debug.LogWarning("[CFZ-Offline][bundle] 含 swat 的 chart 条目数=" + n);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][bundle] dump 异常: " + e.Message); }
        }

        // ==================== ★相机挂载修复★ 补发 Camera_ChangeViewMode(Normal) ====================
        //
        // 症状: 能看到地图, 但看不到角色/没有枪/鼠标转视角无反应, 相机钉在 (0,0,0)
        // 根因: 没人派发 Camera_ChangeViewMode(Normal, myPlayerID) → SceneCamera 的 _currentController
        //       (SceneCameraNormal) 从未 SetTarget → 相机一动不动。
        //
        // SceneCamera.OnEvent_ChangeViewMode (第310-338行):
        //   Player player = PlayerManager.Instance.GetPlayer(param.PlayerID);
        //   case CameraViewMode.Normal:
        //       _ChangeViewMode(Normal, player, player)        → SceneCameraNormal.SetTarget([player])
        //         → _target = player.CameraNode; _rotation = player.Rotation;
        //           player.OnCameraAttached(); player.SetViewTarget(true);
        //       if (InGame.ModeType == Tutorial) SendEvent(Input_SelectInput_Tutorial);   ★ 输入模式也一起修好
        //
        // Player.OnCameraAttached (第1931-1941行):
        //   OnShowPVModel(true)                                  ← ★ 手里的枪出现
        //   Model.QV_OnShow(false)
        //   Rotation.OnInit(..., SceneCamera.CameraTransform, 骨骼...)  ← ★ 鼠标视角开始工作
        //   SendEvent(InGameUI_ShowCrossHair)                    ← ★ 准星出现
        //   OnChangeZoomStep(...)

        static int _camAttachTry = 0;
        static int _camAttachErr = 0;
        static bool _camAttachDone = false;

        static void EnsureCameraAttached()
        {
            try
            {
                if (_camAttachDone) return;
                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                if (pm == null) return;
                object mp = Member(pm, "MyPlayer");
                if (mp == null) return;
                object data = Member(mp, "Data");
                if (data == null) return;

                // 已经挂上了? (OnCameraAttached 里会置 IsPVPlayer=true)
                // ★★★ 但光看 IsPVPlayer 不够! PlayerRotation.OnInit 第245-265行:
                //       tSpine = spine; if (tSpine != null) tSpine1 = spine1;
                //       if (tSpine1 != null) tNeck = neck;
                //       if (tNeck != null) IsMyPlayer = isMyPlayer;      ← ★ 骨骼全绑上才置 IsMyPlayer
                //   而 OnCameraAttached 传的是 Model.QV_GetMiddleBonePelvis/Spine/Spine1/Neck()。
                //   若派发时机早于模型骨骼就绪 → 传入 null → IsMyPlayer 永远 false
                //     → PlayerRotation.LateUpdate 第152行 if (IsMyPlayer) 整块被跳过
                //     → CharacterRotation 恒为 identity
                //     → 相机 rotation = identity*identity → ★ 视角永远不转
                //     → myPlayer.PhysicalObject.Rotation 不更新 → ★ 移动方向冻结在出生朝向
                // 所以"收工"的条件必须是: IsPVPlayer==true ★且★ Rotation.IsMyPlayer==true
                object pv = Member(mp, "IsPVPlayer");
                object rot = Member(mp, "Rotation");
                bool pvOK = pv is bool && (bool)pv;
                object isMy = rot != null ? Member(rot, "IsMyPlayer") : null;
                object tNeck = rot != null ? Member(rot, "tNeck") : null;
                object tSpine = rot != null ? Member(rot, "tSpine") : null;
                bool rotOK = (isMy is bool && (bool)isMy) || tNeck != null;
                if (pvOK && rotOK)
                {
                    _camAttachDone = true;
                    Debug.Log("[CFZ-Offline][相机修复] 相机已挂载且骨骼已绑定 (IsPVPlayer=True, Rotation.IsMyPlayer=" +
                              isMy + ", tSpine=" + (tSpine != null ? "有" : "★无★") + ") → 收工");
                    return;
                }

                object pidObj = Member(data, "PlayerID");
                if (pidObj == null) return;
                long pid = Convert.ToInt64(pidObj);

                if (_camAttachTry > 60) return;
                _camAttachTry++;
                if (_camAttachTry % 60 != 0 && _camAttachTry != 1) return;   // 每 60 帧重试一次, 等骨骼就绪

                var p = new CFW.Framework.EventParam_Camera_ChangeViewMode();
                p.ViewMode = CFW.InGame.GameCamera.CameraViewMode.Normal;
                p.PlayerID = pid;
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Camera_ChangeViewMode, p);

                object pv2 = Member(mp, "IsPVPlayer");
                object isMy2 = rot != null ? Member(rot, "IsMyPlayer") : null;
                object tNeck2 = rot != null ? Member(rot, "tNeck") : null;
                if (_camAttachTry <= 2 || _camAttachTry % 30 == 0)
                    Debug.LogWarning("[CFZ-Offline][相机修复] 派发 Camera_ChangeViewMode(Normal, pid=" + pid +
                                     ") 第 " + _camAttachTry + " 次 | 派发后 IsPVPlayer=" + pv2 +
                                     " IsMyPlayer=" + isMy2 + " tNeck=" + (tNeck2 != null ? "有" : "★无★"));
            }
            catch (Exception e)
            {
                if (_camAttachErr++ < 3)
                    Debug.LogError("[CFZ-Offline][相机修复] 异常: " +
                        (e.InnerException != null ? e.InnerException.ToString() : e.Message));
            }
        }

        // ==================== ★回合修复★ 补发 Network_RecvRoundStart ====================
        //
        // InGame.cs 第300-307行 OnEvent_RoundStart (由 GameEvent.Network_RecvRoundStart 触发):
        //     InGameMode.OnRoundStarting();                                   ← ★ 回合开始 → 敌人生成
        //     SendEvent(GameEvent.Input_SetMovingEnable, null);               ← ★★ 移动使能
        //     SendEvent(GameEvent.InGameUI_RoundStart, null);
        //     InGameUtil.PlayerRadioSound(InGameUtil.GetRoundStartSound());   ← ★★ 回合开始音效/语音
        // 离线永远收不到这个网络事件 → WorldState 停在 ROUND_INIT
        //   → 没有敌人 / 没有声音 / 移动没被正式使能 ("移动方向错误")
        // 修复: 相机挂好后自己补发 (两个 OnEvent 都没用 param, 传 null 安全)。

        static int _roundStartFrames = 0;
        static int _roundStartSent = 0;
        static int _roundStartErr = 0;

        static string ReadWorldState()
        {
            try
            {
                var f = typeof(InGameModeBase).GetField("WorldState",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return "?";
                object v = f.GetValue(null);
                return v != null ? v.ToString() : "?";
            }
            catch { return "?"; }
        }

        static void EnsureRoundStart()
        {
            try
            {
                if (_roundStartSent >= 5) return;
                _roundStartFrames++;
                if (_roundStartFrames < 60) return;                       // 等玩家/相机稳定
                if ((_roundStartFrames - 60) % 180 != 0) return;          // 之后每 180 帧试一次

                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                object mp = pm != null ? Member(pm, "MyPlayer") : null;
                if (mp == null || Member(mp, "Data") == null) return;

                string ws = ReadWorldState();
                if (ws != "ROUND_INIT") return;                           // 已经开始了 → 不动它

                _roundStartSent++;
                Debug.LogWarning("[CFZ-Offline][回合修复] WorldState=" + ws +
                                 " → 补发 Network_RecvRoundInit + Network_RecvRoundStart (第 " + _roundStartSent + " 次)");
                try { CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Network_RecvRoundInit, null); }
                catch (Exception e1) { Debug.LogWarning("[CFZ-Offline][回合修复] RoundInit 异常: " + e1.Message); }
                try { CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Network_RecvRoundStart, null); }
                catch (Exception e2) { Debug.LogWarning("[CFZ-Offline][回合修复] RoundStart 异常: " + e2.Message); }
                Debug.LogWarning("[CFZ-Offline][回合修复] 派发后 WorldState=" + ReadWorldState());
            }
            catch (Exception e)
            {
                if (_roundStartErr++ < 3) Debug.LogError("[CFZ-Offline][回合修复] 异常: " + e.Message);
            }
        }

        // ==================== ★运行时修复★ 每帧巡检 (视角/音频/相机抢占) ====================
        //
        // 1) PlayerRotation.isEnable  ★本轮最关键★
        //    Player.cs 第759-800行 Player.OnRespawn:
        //        784:  Shot.OnRespawn();          ← 抛 NRE (TLog, clientToWorldProxy==null)
        //        796:  Rotation.OnRespawn(..., IsMyPlayer);   ← ★ 永远到不了
        //    PlayerRotation.OnRespawn 第272-284行 → SetEnable(isEnable: true)
        //    → isEnable 永远 false → LateUpdate 第148行 if (!isEnable) return
        //      → CharacterRotation 不再更新 → 相机 rotation 冻结 (视角不转)
        //      → myPlayer.PhysicalObject.Rotation 不再更新 (移动方向冻结在出生朝向)
        //    修复: 巡检到 false 就自己置 true (TLog 兜底生效后游戏也会自己置)
        //
        // 2) AudioListener=0 → 全程无声 (进图后监听器被销毁且没重建) → 补挂一个
        //
        // 3) UE_Freecam 寄生相机抢渲染 (SceneCamera 自身相机 on=False) → 禁用 + 把 SceneCamera 抢回来

        static bool _audioFixed = false;
        static int _runtimeFrame = 0;
        static int _runtimeErr = 0;
        static int _freecamKills = 0;
        static int _pvProbeFrame = 0;
        static bool _rotEnableLogged = false;
        static int _fpvTry = 0;
        static int _playerProbeFrame = 0;
        static bool _pvShown = false;
        static int _fpvChgLog = 0;

        // 把 t 子树里所有处于 "Hidden" 层的物体改回 PV 层, 返回改动数量 (-1 = t 为 null)
        static int SetSubtreeLayer(UnityEngine.Transform t, int pvLayer, int hidLayer)
        {
            if (t == null) return -1;
            int n = 0;
            var trs = t.GetComponentsInChildren<UnityEngine.Transform>(true);
            for (int i = 0; i < trs.Length; i++)
            {
                if (trs[i].gameObject.layer == hidLayer) { trs[i].gameObject.layer = pvLayer; n++; }
            }
            return n;
        }

        // ==================== ★声音修复★ Option 音量全空 → 每次音量都被乘成 0 ====================
        //
        // 病根与本工程"键位表整张为空"同源: Option 数据整体缺失
        //   (Option.Load: 只 LoadDefault 不填 Value; Option.xml 不存在时直接 return;
        //    连 DefaultOption 的 Default 也没灌进来 —— 实测 GetDefaultKey 也是 None)
        //
        // 两道静音闸门 (任一中招 → 全程无声):
        //  ① AudioEventHandler.OnPlaySound (AudioEventHandler.cs:46-58):
        //       num = 事件音量 × Option.Sound.Common.Effect.Volume.Value × 0.01f
        //                        × Option.Sound.Common.Volume.Volume.Value  × 0.01f;
        //       if (Effect.IsMute.Value || Volume.IsMute.Value) num = 0f;
        //     → Volume.Value 为 0 → 每次播放音量都是 0
        //  ② CommonOption.ApplySound (CommonOption.cs:463-471) / Sound.Cancel (Sound.cs:55-57):
        //       AudioManager.Instance.SetMainMixerVolume("MasterVol", Volume.Volume.Value);  ← ★ 0 → -80dB
        //       AudioManager.Instance.SetMainMixerVolume("EFXVol",    Effect.Volume.Value);
        //     SetMainMixerVolume (AudioManager.cs:338-343): vol==0 → 直接 -80dB (等于整机静音)
        //  ③ AudioManager.Listener (AudioManager.cs:42) 为 null → _PlayAudioInfo:230 直接 NRE
        //     ★给 SceneCamera 挂 Unity 的 AudioListener ≠ 给这个字段赋值★
        // 修复: 4 个音量置 100 + 取消 4 个静音 → 重新 ApplySound(); 再把 Listener 字段补上。

        static bool _soundFixed = false;
        static int _soundProbeFrame = 0;                        // (已停用, 留着防误删引用)
        static bool _soundListenerDone = false;                 // Listener 补挂: 每局"我进图后"执行一次

        static void EnsureSoundFix()
        {
            try
            {
                if (!_soundFixed)
                {
                    _soundFixed = true;
                    var sc = Common.System.Option.Option.Sound.Common;
                    string before = "主=" + sc.Volume.Volume.Value + "(mute=" + sc.Volume.IsMute.Value + ")" +
                                    " 音效=" + sc.Effect.Volume.Value + "(mute=" + sc.Effect.IsMute.Value + ")" +
                                    " BGM=" + sc.BGM.Volume.Value + "(mute=" + sc.BGM.IsMute.Value + ")" +
                                    " 电台=" + sc.RadioMessage.Volume.Value + "(mute=" + sc.RadioMessage.IsMute.Value + ")";

                    sc.Volume.Volume.Value = 100; sc.Volume.IsMute.Value = false;
                    sc.Effect.Volume.Value = 100; sc.Effect.IsMute.Value = false;
                    sc.BGM.Volume.Value = 100; sc.BGM.IsMute.Value = false;
                    sc.RadioMessage.Volume.Value = 100; sc.RadioMessage.IsMute.Value = false;

                    bool applied = false;
                    try { Common.System.Option.CommonOption.ApplySound(); applied = true; }
                    catch (Exception e2) { Debug.LogWarning("[CFZ-Offline][声音] CommonOption.ApplySound 失败: " + e2.Message); }
                    if (!applied)
                    {
                        // 兜底: 直接把混音器音量顶回去 (vol==0 会被 SetMainMixerVolume 判成 -80dB)
                        try
                        {
                            var am0 = CFW.GameSystem.Audio.AudioManager.Instance;
                            if (am0 != null)
                            {
                                am0.SetMainMixerVolume("MasterVol", 100);
                                am0.SetMainMixerVolume("EFXVol", 100);
                                applied = true;
                            }
                        }
                        catch { }
                    }
                    Debug.LogWarning("[CFZ-Offline][声音] Option 音量修复: " + before +
                                     " → 全部 100/取消静音 | ApplySound=" + applied);
                }

                // AudioManager.Listener 补挂 + 全局音量 —— ★初始化阶段执行一次★ (2026-10-09)
                //   监听器在进图加载时被销毁 → "我"(MyPlayer)出现时销毁必然已发生, 此刻补挂一次即可。
                //   不再每 300 帧周期复查 (旧版开局要等最多 300 帧才第一次查 → 开头几秒无声)。
                //   每局 F9 时 _soundListenerDone 被复位, 下一局进图再执行一次。
                if (_soundListenerDone) return;
                var pmi = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                object mpi = (pmi != null) ? Member(pmi, "MyPlayer") : null;
                if (mpi == null) return;                       // 还没进图: 继续等, 不算"执行过"
                _soundListenerDone = true;
                var am = CFW.GameSystem.Audio.AudioManager.Instance;
                if (am == null)
                {
                    Debug.LogWarning("[CFZ-Offline][声音] ★AudioManager.Instance=null★ → 音频系统根本没起来");
                    return;
                }
                var alType = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                int cnt = 0;
                bool lisOK = Member(am, "Listener") != null;
                if (alType != null)
                {
                    var arr = UnityEngine.Object.FindObjectsOfType(alType);
                    cnt = arr.Length;
                    if (!lisOK)
                    {
                        object pick = cnt > 0 ? arr[0] : null;
                        if (pick == null)
                        {
                            object sc = null;
                            try { sc = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera; } catch { }
                            var scc = sc as UnityEngine.Component;
                            if (scc != null) { pick = scc.gameObject.AddComponent(alType); cnt = 1; }
                        }
                        if (pick != null)
                        {
                            var f = am.GetType().GetField("Listener", F2);
                            if (f != null)
                            {
                                f.SetValue(am, pick);
                                lisOK = true;
                                Debug.LogWarning("[CFZ-Offline][声音] AudioManager.Listener 原为 null → 已赋值 (场景监听器数=" + cnt + ")");
                            }
                        }
                    }
                    try
                    {
                        var vp = alType.GetProperty("volume", BindingFlags.Public | BindingFlags.Static);
                        if (vp != null && Convert.ToSingle(vp.GetValue(null, null)) < 0.001f)
                        {
                            vp.SetValue(null, 1f, null);
                            Debug.LogWarning("[CFZ-Offline][声音] AudioListener.volume=0 → 已置 1");
                        }
                    }
                    catch { }
                }
                Debug.Log("[CFZ-Offline][声音] AudioManager=有 Listener=" + (lisOK ? "有" : "★null★") +
                          " 场景监听器=" + cnt +
                          " 主音量=" + Common.System.Option.Option.Sound.Common.Volume.Volume.Value +
                          " 音效=" + Common.System.Option.Option.Sound.Common.Effect.Volume.Value +
                          " | 已收到 PlayAudio 事件=" + _audioEvt + " 实际播放调用=" + _sndPlay +
                          " 特效请求=" + _fxReq + " 弹着点=" + _hitFx);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][声音] 修复异常: " + e.Message); }
        }

        // ==================== ★亮度修复★ 暗屏 ====================================================
        //
        //  现象: 进游戏后整个画面暗一半 (SceneCamera 上那个 CFW.ScreenEffects.ChangeCameraBright 没被关掉)
        //  真因 (CommonOption.cs:449-461 + ChangeCameraBright.cs:19-23):
        //      ApplyBright():  bright = Option.Graphic.Brightness.Value / 100f + 0.5f;
        //                      foreach (var c in FindObjectsOfType<ChangeCameraBright>()) c.Bright = bright;
        //      OnRenderImage(): material.SetFloat("_Bright", Bright); Graphics.Blit(source, destination, material);
        //  ★Option 数据整体缺失 (与"键位表/音量全空"同一病根) → Brightness=0
        //      → bright = 0 + 0.5 = **0.5 → 整屏亮度砍一半** (实测日志: Brightness=0, ChangeCameraBright:[enabled=True Bright=0.5])
        //  修法: Brightness 置 50 (=中性 1.0) 并重跑 ApplyBright; 之后万一被别处改回 <0.95 再拉回 1.0。

        static bool _graphicFixed = false;
        static int _graphicProbeFrame = 0;

        static void EnsureGraphicFix()
        {
            try
            {
                var g = Common.System.Option.Option.Graphic;

                if (!_graphicFixed || g.Brightness.Value == 0)
                {
                    int before = g.Brightness.Value;
                    if (before == 0) g.Brightness.Value = 50;          // 50 → 0.5 + 0.5 = 1.0 (中性, 不变亮也不变暗)
                    try { Common.System.Option.CommonOption.ApplyBright(); }
                    catch (Exception e1) { Debug.LogWarning("[CFZ-Offline][亮度] ApplyBright 失败: " + e1.Message); }
                    if (!_graphicFixed)
                    {
                        _graphicFixed = true;
                        var cc0 = UnityEngine.Object.FindObjectsOfType<CFW.ScreenEffects.ChangeCameraBright>();
                        Debug.LogWarning("[CFZ-Offline][亮度] Option.Graphic.Brightness=" + before + " → " + g.Brightness.Value +
                                         " (50=中性1.0) | ChangeCameraBright 实例=" + cc0.Length +
                                         (cc0.Length > 0 ? " 第一个.Bright=" + cc0[0].Bright : ""));
                    }
                }

                // ★周期复查已停用★ (2026-10-09): Bright 偏暗拉回中性的每 300 帧复查不再执行,
                //   只保留上面的一次性修复 (Brightness=0 → 50, 关掉会黑屏)。
                return;

                // 兜底: 选项已是中性(50) 但某个实例的 Bright 仍 < 0.95 → 说明别处把它改暗了, 拉回中性
                var cc = UnityEngine.Object.FindObjectsOfType<CFW.ScreenEffects.ChangeCameraBright>();
                int fixedN = 0;
                if (g.Brightness.Value == 50)
                {
                    for (int i = 0; i < cc.Length; i++)
                    {
                        if (cc[i] == null) continue;
                        if (cc[i].Bright < 0.95f) { cc[i].Bright = 1f; fixedN++; }
                    }
                }
                if (fixedN > 0)
                    Debug.LogWarning("[CFZ-Offline][亮度] ChangeCameraBright.Bright 偏暗 → 已拉回 1.0 × " + fixedN +
                                     " (实例=" + cc.Length + " Option.Brightness=" + g.Brightness.Value + ")");
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline][亮度] 修复异常: " + e.Message); }
        }

        static void EnsureRuntimeFixes()
        {
            try
            {
                _runtimeFrame++;
                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                object mp = pm != null ? Member(pm, "MyPlayer") : null;

                // ---- 1) ★PlayerRotation.isEnable 巡检★ 已停用 (2026-10-09) ----
                //   原逻辑: 每 120 帧发现 isEnable=false 就强制置 true。
                //   ⚠ 日志(15664)显示它曾实际救场: 复活的 OnRespawn 仍会被 NRE 打断 → Rotation.OnRespawn
                //     到不了 → isEnable=false → LateUpdate 直接 return → 视角冻死。
                //   停用后若死亡复活/开局"视角转不动", 就是它 —— 反馈我还原。
                // if (mp != null && (_runtimeFrame % 120 == 1))
                // {
                //     object rot = Member(mp, "Rotation");
                //     if (rot != null)
                //     {
                //         object en = Member(rot, "isEnable");
                //         if (en is bool && !(bool)en)
                //         {
                //             var sm = rot.GetType().GetMethod("SetEnable", F2);
                //             if (sm != null)
                //             {
                //                 sm.Invoke(rot, new object[] { true });
                //                 if (!_rotEnableLogged)
                //                 {
                //                     _rotEnableLogged = true;
                //                     Debug.LogWarning("[CFZ-Offline][运行时修复] PlayerRotation.isEnable=false (OnRespawn 被 TLog NRE 打断) → 已强制置 true" +
                //                      " → isEnable=" + Member(rot, "isEnable"));
                //                 }
                //             }
                //         }
                //     }
                // }

                // ---- 2) 补 AudioListener ----
                if (!_audioFixed)
                {
                    var alType = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                    if (alType != null)
                    {
                        var exist = UnityEngine.Object.FindObjectsOfType(alType);
                        if (exist.Length > 0) _audioFixed = true;
                        else
                        {
                            object sc = null;
                            try { sc = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera; } catch { }
                            var scc = sc as UnityEngine.Component;
                            if (scc != null)
                            {
                                scc.gameObject.AddComponent(alType);
                                _audioFixed = true;
                                Debug.LogWarning("[CFZ-Offline][运行时修复] 场景 AudioListener=0 → 已在 '" + scc.gameObject.name + "' 上补挂 (恢复声音)");
                            }
                        }
                    }
                }

                // ---- 3) 干掉 UE_Freecam, 并把 SceneCamera 的相机抢回启用 ----
                try
                {
                    var cams = UnityEngine.Camera.allCameras;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var c = cams[i];
                        if (c == null) continue;
                        if (c.name == "UE_Freecam" && c.enabled)
                        {
                            c.enabled = false;
                            if (_freecamKills++ < 5)
                                Debug.LogWarning("[CFZ-Offline][运行时修复] UE_Freecam 抢了渲染 (mask=" + c.cullingMask +
                                                 ") → 已禁用 (第 " + _freecamKills + " 次)");
                        }
                    }
                    object sc3 = null;
                    try { sc3 = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera; } catch { }
                    var scc3 = sc3 as UnityEngine.Component;
                    if (scc3 != null)
                    {
                        var cam3 = scc3.GetComponent<UnityEngine.Camera>();
                        if (cam3 != null && !cam3.enabled)
                        {
                            cam3.enabled = true;
                            Debug.LogWarning("[CFZ-Offline][运行时修复] SceneCamera 自身相机被禁用 → 已重新启用");
                        }
                    }
                }
                catch { }

                // ---- 4) PV 模型 / 当前武器 探针 (说明"没有武器"缺哪一块) ----
                if (mp != null && (++_pvProbeFrame % 300 == 0))
                {
                    object mdl = Member(mp, "Model");
                    if (mdl != null)
                    {
                        object pvModel = null, curWp = null;
                        try { var g = mdl.GetType().GetMethod("GetPVModel", F2); if (g != null) pvModel = g.Invoke(mdl, null); } catch { }
                        try { var g = mdl.GetType().GetMethod("GetCurrentWeapon", F2); if (g != null) curWp = g.Invoke(mdl, null); } catch { }
                        var pvt = pvModel as UnityEngine.Component;
                        string pvs = "★null★";
                        if (pvt != null)
                        {
                            var rs = pvt.GetComponentsInChildren<UnityEngine.Renderer>(true);
                            int on = 0;
                            for (int i = 0; i < rs.Length; i++) if (rs[i].enabled) on++;
                            pvs = "有 pos=" + pvt.transform.position.ToString("F1") +
                                  " active=" + pvt.gameObject.activeSelf + " layer=" + pvt.gameObject.layer +
                                  " Renderer=" + rs.Length + "(on=" + on + ")";
                        }
                        Debug.LogWarning("[CFZ-Offline][运行时修复][PV] GetPVModel=" + pvs +
                                         " | GetCurrentWeapon=" + (curWp != null ? "有" : "★null★") +
                                         " | 物理朝向=" + Member(Member(mp, "PhysicalObject"), "Rotation"));
                    }
                }

                // ---- 5) ★★★第一人称武器★★★ 把 PV 的层从 "Hidden" 抢回 "PV" ----
                //
                // 机制 (PlayerModel.PV_OnShow → PVModel.OnShow → _ShowCurrentWeapon):
                //     Hand.OnShow(isShow);  pV_Weapon.OnShow(isShow);
                //   PV_Hand / PV_Weapon 的 Show 只做一件事 (★不改 activeSelf / Renderer.enabled):
                //     gameObject.layer = isShow ? NameToLayer("PV") : NameToLayer("Hidden");
                // 实测: PV的Renderer层=[10,10,0,10,10] ← 全是 10 = "Hidden"
                //   主相机 mask=-1025 排除 bit10 ; FPVCamera mask=256 只含 bit8
                //   → 两台相机都不渲染 layer10 → ★第一人称武器必然隐形★ = "第一人称没有武器"
                // 根因: OnShowPVModel(true) 早在 PV_Arm 异步加载完成之前就跑了;
                //       后来由 PV_Create_Hand 挂进来的 ARM 物体带着 Hidden 层, 没人再 Show 一次。
                // 修复:
                //   (a) IsCreatePVArmComplete 变 true 后再重申 OnShowPVModel(true) + ShowQVModel(false)
                //   (b) 兜底: 只把 Hand + 当前武器 子树里 Hidden 的物体改回 "PV"
                //       ★只改这两个子树, 不动其它武器槽 (否则所有枪会一起显形)
                //   (c) FPVCamera 对准 "PV" 层 + clearFlags=Depth (Skybox/SolidColor 会擦掉整个世界!)
                //       + depth=主相机+1 + nearClipPlane 调小, 然后启用
                // ★ 死亡时必须跳过本块: 它会把 Hand/当前武器 的层从 "Hidden" 抢回 "PV" (等于强制显形),
                //   而死亡流程要求第一人称手/枪保持隐藏 (见 EnsureModelVisibility)
                var myPlayerRef = mp as CFW.InGame.Player.Player;
                if (myPlayerRef != null && !myPlayerRef.IsDeadState && _runtimeFrame % 60 == 1)
                {
                    _fpvTry++;
                    object mdlF = Member(mp, "Model");
                    object pvF = null;
                    if (mdlF != null)
                    {
                        try { var g = mdlF.GetType().GetMethod("GetPVModel", F2); if (g != null) pvF = g.Invoke(mdlF, null); } catch { }
                    }
                    var pvtF = pvF as UnityEngine.Component;
                    object scF = null;
                    try { scF = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera; } catch { }
                    var sccF = scF as UnityEngine.Component;
                    if (pvtF != null && sccF != null)
                    {
                        int pvLayer = UnityEngine.LayerMask.NameToLayer("PV");
                        int hidLayer = UnityEngine.LayerMask.NameToLayer("Hidden");
                        if (pvLayer < 0) pvLayer = 8;                       // 兜底: PV 根节点实测在 8

                        // (a) ARM 加载完成后重申一次
                        object armDone = mdlF != null ? Member(mdlF, "IsCreatePVArmComplete") : null;
                        if (armDone is bool && (bool)armDone && !_pvShown)
                        {
                            try { var m = mp.GetType().GetMethod("OnShowPVModel", F2); if (m != null) m.Invoke(mp, new object[] { true }); } catch { }
                            try { var m = mp.GetType().GetMethod("ShowQVModel", F2); if (m != null) m.Invoke(mp, new object[] { false }); } catch { }
                            _pvShown = true;
                            Debug.LogWarning("[CFZ-Offline][运行时修复][PV层] IsCreatePVArmComplete=true → 重申 OnShowPVModel(true) + ShowQVModel(false)");
                        }

                        // (b) 兜底改层 ★已停用★ (2026-10-09 狙击开镜排查)
                        //     狙击开镜会把 手/当前武器 子树改到 Hidden (镜内视野干净),
                        //     这个兜底每 60 帧(≈1秒)把它们抢回 "PV" → "开镜 1 秒后第一人称模型又出现"。
                        //     先整体停用测试; 若武器模型因此隐身, 再改成"开镜(CurrentZoomStep>0)时跳过"。
                        int movedH = 0, movedW = 0;

                        // (c) FPVCamera
                        var mainCam = sccF.GetComponent<UnityEngine.Camera>();
                        var fpvTr = sccF.transform.Find("FPVCamera");
                        var fpvc = fpvTr != null ? fpvTr.GetComponent<UnityEngine.Camera>() : null;
                        if (_fpvTry <= 2 || movedH > 0 || movedW > 0)
                        {
                            var rsF = pvtF.GetComponentsInChildren<UnityEngine.Renderer>(true);
                            var layerSb = new System.Text.StringBuilder();
                            for (int i = 0; i < rsF.Length; i++) layerSb.Append(rsF[i].gameObject.layer).Append(',');
                            Debug.LogWarning("[CFZ-Offline][运行时修复][PV层] PV.Renderer层=[" + layerSb + "] (PV层=" + pvLayer +
                                             " Hidden层=" + hidLayer + ") | 手子树改层=" + movedH + " 当前武器改层=" + movedW +
                                             " | 主相机mask=" + (mainCam != null ? mainCam.cullingMask.ToString() : "?") +
                                             " near=" + (mainCam != null ? mainCam.nearClipPlane.ToString("F2") : "?") +
                                             " far=" + (mainCam != null ? mainCam.farClipPlane.ToString("F0") : "?") +
                                             " | FPVCamera=" + (fpvc != null
                                                ? ("mask=" + fpvc.cullingMask + " on=" + fpvc.enabled + " clear=" + fpvc.clearFlags +
                                                   " depth=" + fpvc.depth + " near=" + fpvc.nearClipPlane.ToString("F2"))
                                                : "★无★"));
                        }
                        if (fpvc != null)
                        {
                            bool changed = false;
                            int want = 1 << pvLayer;
                            if (fpvc.cullingMask != want) { fpvc.cullingMask = want; changed = true; }
                            // ★Skybox / SolidColor 都会把主相机画好的世界擦掉 → 必须 Depth
                            if (fpvc.clearFlags != UnityEngine.CameraClearFlags.Depth)
                            { fpvc.clearFlags = UnityEngine.CameraClearFlags.Depth; changed = true; }
                            float md = mainCam != null ? mainCam.depth : 0f;
                            if (fpvc.depth <= md) { fpvc.depth = md + 1f; changed = true; }
                            if (fpvc.nearClipPlane > 0.05f) { fpvc.nearClipPlane = 0.01f; changed = true; }
                            if (!fpvc.enabled) { fpvc.enabled = true; changed = true; }
                            if (changed && (_fpvTry <= 2 || _fpvChgLog < 3))
                            {
                                _fpvChgLog++;
                                Debug.LogWarning("[CFZ-Offline][运行时修复][PV层] FPVCamera 配置: mask=" + fpvc.cullingMask +
                                                 " clear=" + fpvc.clearFlags + " depth=" + fpvc.depth +
                                                 " near=" + fpvc.nearClipPlane.ToString("F2") + " on=" + fpvc.enabled);
                            }
                        }
                    }
                }

                // ---- 6) ★敌人详单★ 5 个玩家到底在哪、QV 模型有没有被藏起来 ----
                if ((++_playerProbeFrame % 300 == 0))
                {
                    try
                    {
                        var pls = UnityEngine.Object.FindObjectsOfType<CFW.InGame.Player.Player>();
                        var sb = new System.Text.StringBuilder("[CFZ-Offline][运行时修复][敌人] 玩家数=" + pls.Length);
                        UnityEngine.Vector3 myPos = mp is UnityEngine.Component ? ((UnityEngine.Component)mp).transform.position : UnityEngine.Vector3.zero;
                        for (int i = 0; i < pls.Length && i < 6; i++)
                        {
                            var p = pls[i];
                            if (p == null) continue;
                            object d = Member(p, "Data");
                            object isMy = d != null ? Member(d, "IsMyPlayer") : null;
                            object mdl = Member(p, "Model");
                            object qv = null;
                            if (mdl != null) { try { var g = mdl.GetType().GetMethod("GetQVModel", F2); if (g != null) qv = g.Invoke(mdl, null); } catch { } }
                            var qvt = qv as UnityEngine.Component;
                            string qvs = "★null★";
                            if (qvt != null)
                            {
                                var rs = qvt.GetComponentsInChildren<UnityEngine.Renderer>(true);
                                int on = 0;
                                for (int k = 0; k < rs.Length; k++) if (rs[k].enabled) on++;
                                int lay = rs.Length > 0 ? rs[0].gameObject.layer : qvt.gameObject.layer;
                                qvs = "有 active=" + qvt.gameObject.activeInHierarchy + " layer=" + lay + " R=" + rs.Length + "(on=" + on + ")";
                            }
                            float dist = UnityEngine.Vector3.Distance(p.transform.position, myPos);
                            sb.Append(" [").Append(i).Append(']').Append(p.name)
                              .Append(isMy is bool && (bool)isMy ? "(我)" : "")
                              .Append(" pos=").Append(p.transform.position.ToString("F0"))
                              .Append(" 距我=").Append(dist.ToString("F0")).Append("m")
                              .Append(" QV=").Append(qvs)
                              // ---- ★巡逻诊断★ bot 走不动卡在哪一步 ----
                              .Append(" | Δ=").Append(MovedDelta(i, p.transform.position).ToString("F1")).Append("m")
                              .Append(" AI[").Append(AiState(p)).Append(']')
                              .Append(" 落地=").Append(HasNaviGround(p.transform.position) ? "有" : "★无★")
                              .Append(" A*:").Append(AStarInfo(p.transform.position));
                        }
                        _haveBotPos = true;
                        int gN = 0;
                        try
                        {
                            var asp = AstarPath.active;
                            if (asp != null && asp.data != null && asp.data.graphs != null) gN = asp.data.graphs.Length;
                        }
                        catch { }
                        sb.Append(" || A*导航图=").Append(gN).Append(" 个");
                        Debug.LogWarning(sb.ToString());

                        // ---- ★巡逻兜底★ 已停用 (2026-10-09) ----
                        // for (int i = 0; i < pls.Length; i++)
                        // {
                        //     var pn = pls[i];
                        //     if (pn == null) continue;
                        //     object dn = Member(pn, "Data");
                        //     object imy = dn != null ? Member(dn, "IsMyPlayer") : null;
                        //     if (imy is bool && (bool)imy) continue;
                        //     if (pn.transform.position.y < myPos.y - 25f) continue;    // 正掉下去的交给掉落修复
                        //     PatrolNudge(pn);
                        // }
                    }
                    catch (Exception ep) { Debug.LogWarning("[CFZ-Offline][运行时修复][敌人] 探针异常: " + ep.Message); }
                }

                // ---- 6-2) ★bot 掉出地图修复★ 换图后刷点可能在地板下方 → bot 一路下坠(地图上看不到敌人) ----
                if ((++_botFixFrame % 60) == 0) BotDropFix(mp);

                // ---- 6-3) ★武器袋 UI 补初始化★ (B 键呼出背包) ----
                if ((++_bagUiFrame % 120) == 0) BagUiFix(mp);

                // ---- 7) 音频探针 ----
                if (_runtimeFrame % 300 == 2)
                {
                    try
                    {
                        var alT = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                        string vs = "?";
                        if (alT != null)
                        {
                            var vp = alT.GetProperty("volume", BindingFlags.Public | BindingFlags.Static);
                            if (vp != null) vs = vp.GetValue(null, null).ToString();
                            var srcT = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
                            int tot = 0, playing = 0;
                            if (srcT != null)
                            {
                                var arr = UnityEngine.Object.FindObjectsOfType(srcT);
                                tot = arr.Length;
                                var pprop = srcT.GetProperty("isPlaying", BindingFlags.Public | BindingFlags.Instance);
                                if (pprop != null)
                                {
                                    for (int i = 0; i < arr.Length && i < 300; i++)
                                    {
                                        try { if (pprop.GetValue(arr[i], null) is bool && (bool)pprop.GetValue(arr[i], null)) playing++; }
                                        catch { }
                                    }
                                }
                            }
                            Debug.LogWarning("[CFZ-Offline][运行时修复][音频] AudioListener.volume=" + vs + " AudioSource数=" + tot + " 正在播放=" + playing);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                if (_runtimeErr++ < 3)
                    Debug.LogError("[CFZ-Offline][运行时修复] 异常: " +
                        (e.InnerException != null ? e.InnerException.ToString() : e.Message));
            }
        }

        // ==================== ★移动兜底★ Shot == null 时自推坐标 ====================
        //
        // Player.cs 第433-449行 PlayerMoveFixedUpdate:
        //     if (!IsLoading) {
        //         MovementController.UpdateAll(deltaTime, IsMyPlayer);
        //         if (Shot != null && Data.IsMyPlayer)                                  // ★★★ 这道门
        //             MovementController.UpdateMyPlayer(deltaTime, GetSpeedPenaltyByWeapon(),
        //                                               Shot.stoppingPower.GetStoppingPowerMovePenatlyRatio());
        //     }
        // QV 模型缺失 → Shot == null → UpdateMyPlayer 永不调用 → 坐标永远冻结。
        // 兜底: 在 postfix 里补一次调用 (UpdateAll 原逻辑已跑, 顺序无害)。
        static int _moveBypassCount = 0;

        public static void PlayerMoveFixedUpdatePostfix(CFW.InGame.Player.Player __instance, float deltaTime)
        {
            try
            {
                if (__instance == null) return;
                if (Member(__instance, "Shot") != null) return;          // 正常路径已处理
                object mc = Member(__instance, "MovementController");
                if (mc == null) return;
                object data = Member(__instance, "Data");
                if (data == null) return;
                object my = Member(data, "IsMyPlayer");
                if (!(my is bool) || !(bool)my) return;
                object dead = Member(__instance, "IsDeadState");
                if (dead is bool && (bool)dead) return;
                object loading = Member(__instance, "IsLoading");
                if (loading is bool && (bool)loading) return;

                var um = mc.GetType().GetMethod("UpdateMyPlayer", F2);
                if (um == null) return;
                // 速度异常 (自由落体 / 物理爆炸) → 归零, 避免 LTPhysics 在极端速度下抛异常
                object pobj0 = Member(mc, "_pObj");
                if (pobj0 != null)
                {
                    object v0 = Member(pobj0, "Velocity");
                    if (v0 is UnityEngine.Vector3)
                    {
                        UnityEngine.Vector3 v = (UnityEngine.Vector3)v0;
                        if (v.sqrMagnitude > 160000f)                       // |v| > 400 (缩放单位)
                        {
                            SetVec(pobj0, "Velocity", UnityEngine.Vector3.zero);
                            if (_velClampCount++ < 5)
                                Debug.LogWarning("[CFZ-Offline][移动兜底] 速度异常 " + v + " → 已归零 (防物理爆炸)");
                        }
                    }
                }

                um.Invoke(mc, new object[] { deltaTime, 0f, 1f });

                _moveBypassCount++;
                if (_moveBypassCount == 1 || _moveBypassCount % 120 == 0)
                {
                    object ratio = null;
                    var gm = mc.GetType().GetMethod("GetMoveRatio", F2);
                    if (gm != null) { try { ratio = gm.Invoke(mc, null); } catch { } }
                    object pobj = Member(mc, "_pObj");
                    Debug.LogWarning("[CFZ-Offline][移动兜底] Shot==null → 自调 UpdateMyPlayer #" + _moveBypassCount +
                                     " dt=" + deltaTime + " GetMoveRatio=" + ratio +
                                     " Pos=" + (pobj != null ? Member(pobj, "Position") : null) +
                                     " Vel=" + (pobj != null ? Member(pobj, "Velocity") : null));
                }
            }
            catch (Exception e)
            {
                if (_moveBypassErrCount++ < 3)
                    Debug.LogError("[CFZ-Offline][移动兜底] 异常: " +
                        (e.InnerException != null ? e.InnerException.ToString() : e.ToString()));
            }
        }

        static int _velClampCount = 0;
        static int _moveBypassErrCount = 0;

        static void SetVec(object o, string name, UnityEngine.Vector3 v)
        {
            try
            {
                var f = o.GetType().GetField(name, F2);
                if (f != null && f.FieldType == typeof(UnityEngine.Vector3)) { f.SetValue(o, v); return; }
                var p = o.GetType().GetProperty(name, F2);
                if (p != null && p.CanWrite && p.PropertyType == typeof(UnityEngine.Vector3)) p.SetValue(o, v, null);
            }
            catch { }
        }

        // ★bundle 名解析★ BundleList.GetBundleInfo 的 postfix
        //   AssetBundleChart._CollectAssetBundleResouceNames: _dicBundleInfo.Add(ResourceName.ToLower(), info)
        //   ResourceName 又被 ResourcePath[0] 剥前缀覆盖 → 键 = "character/fps_m_swat"
        //   而游戏传 'FPS_M_SWAT' → TryGetValue("fps_m_swat") miss → Name 为空 → 游戏认为"没这个 bundle"
        // → _NeedPrefabResource = true → Resources.LoadAsync → null → 模型建不起来 → Shot=null → 动不了
        // 修复: Name 为空时, 按 Name 字段 / 键后缀做模糊解析, 返回真实条目。
        public static void GetBundleInfoPostfix(string bundleName, ref Common.System.AssetBundleInfo __result)
        {
            try
            {
                if (!string.IsNullOrEmpty(__result.Name)) return;
                if (string.IsNullOrEmpty(bundleName)) return;
                string low = bundleName.ToLower();
                int slash = low.LastIndexOf('/');
                string tail = slash >= 0 ? low.Substring(slash + 1) : low;

                var rl = Common.System.ResourceLoader.Instance;
                object bl = rl != null ? Member(rl, "bundleLoader") : null;
                object blist = bl != null ? Member(bl, "bundleList") : null;
                object chart = blist != null ? Member(blist, "bundleChart") : null;
                var dic = (chart != null ? Member(chart, "_dicBundleInfo") : null) as System.Collections.IDictionary;
                if (dic == null || dic.Count == 0) return;

                foreach (System.Collections.DictionaryEntry de in dic)
                {
                    string key = de.Key as string;
                    if (key == null) continue;
                    string nm = Member(de.Value, "Name") as string;
                    string nml = nm != null ? nm.ToLower() : null;
                    bool hit = key == low || key.EndsWith("/" + low)
                            || (nml != null && nml == low)
                            || key.EndsWith("/" + tail)
                            || (nml != null && nml == tail);
                    if (!hit) continue;
                    __result = (Common.System.AssetBundleInfo)de.Value;
                    Debug.LogWarning("[CFZ-Offline][bundle] GetBundleInfo('" + bundleName + "') miss → 模糊解析 key='" +
                                     key + "' Name='" + nm + "' Type=" + Member(de.Value, "BundleType") +
                                     " Path0='" + (Member(de.Value, "ResourcePath") as string[] != null &&
                                                   ((string[])Member(de.Value, "ResourcePath")).Length > 0
                                                   ? ((string[])Member(de.Value, "ResourcePath"))[0] : "") + "'");
                    return;
                }
            }
            catch { }
        }

        // 判断"这个资源确实在 bundle 里" —— 命中就强制走 bundleLoader.Load (否则退回 Resources → null)
        //   ★22c 补漏★ 上一版只认 角色/武器/装备, 把 特效 与 音频 挡在门外, 直接造成三个症状:
        //     无枪口火焰 / 无弹孔 / 无火花: ObjectPoolManager._MakePool (ObjectPoolManager.cs:65-89)
        //         Load<GameObject>("Prefabs/Effects/<FXName>", "") → 闸门不放行
        //         → _NeedPrefabResource=true → Resources.LoadAsync → null → prefab==null
        //         → ★静默 return，onLoad 永不回调★ → FXManager._Play 一次都不执行 (_fxReq=0, 也没有 "Can't find fx")
        //     全程无声: AudioInfo.LoadClip (AudioInfo.cs:62-96)
        //         Load<AudioClip>(bundle='Collision', asset='<SoundKey>.wav') → 闸门不放行
        //         → Resources.LoadAsync → null → _clips[]=null → Loaded=false → AudioManager 不回调 → 静音
        //
        // ★★★ 白名单开关 ★★★
        //   true  = 只放行"确认装在本地包里"的资源类型(角色/武器/装备/特效/音频) —— 保守, 不误伤,
        //           但遇到名字没覆盖的新类型就漏(而且是**静默** null, 查起来很费劲)
        //   false = ★取消白名单★: 不再看 asset 名字, 只要"这个 bundle 能在本地清单里解析出来"就强制走 bundle。
        //           更彻底(UI/贴图/动画/文本/预制体一并走 bundle), 代价是波及面变大 → 需要现场验证。
        //           兜底只剩两道: ① bundle 必须本地清单里有  ② assetName 不能为空
        static readonly bool UseAssetWhitelist = false;    // ← 改 true 即恢复白名单 (readonly 不会被编译期折叠, 免得留死代码)

        static int _gateForce = 0;          // _NeedPrefabResource 被强制成 false 的次数
        static int _gateBundle = 0;         // IsBundle 被强制成 true 的次数
        static int _gateProbe = 0;          // [探针][IsBundle] 采样计数

        static bool IsPrefabLikeAsset(string assetName)
        {
            if (!UseAssetWhitelist) return true;                 // ★ 取消白名单: 一律放行
            if (string.IsNullOrEmpty(assetName)) return false;
            // 角色 / 武器 / 装备 (预制体)
            if (assetName.IndexOf("Characters", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (assetName.IndexOf("Weapons", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (assetName.IndexOf("Equip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (assetName.IndexOf("CHARACTER", StringComparison.Ordinal) >= 0) return true;
            if (assetName.IndexOf("INGAME", StringComparison.Ordinal) >= 0) return true;
            // ★特效 (枪口火焰/弹孔/火花/弹壳/血迹...) — 都在 Bundle/Effect/*.unity3d
            if (assetName.IndexOf("Effects", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // ★音频 — Bundle/Sound/*.unity3d, 资源名形如 '<SoundKey>.wav' / '<BGM>.ogg'
            if (assetName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return true;
            if (assetName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ★修复 1★ _NeedPrefabResource: 清单里有 → 强制 false (=走 bundleLoader.Load)
        public static bool NeedPrefabResourcePrefix(string bundleName, string assetName, ref bool __result)
        {
            try
            {
                if (string.IsNullOrEmpty(assetName)) return true;    // 没资产名 → 保持原逻辑
                string n = LookupBundleName(bundleName);
                LogBundleProbeOnce(bundleName, n);
                if (n == null) return true;                     // 清单里没这个名 → 保持原逻辑
                if (!IsPrefabLikeAsset(assetName)) return true;
                __result = false;                               // 走 bundle
                int c = _gateForce++;
                if (c < 20 || c % 200 == 0)                     // 取消白名单后命中量很大 → 采样, 别刷屏
                    Debug.Log("[CFZ-Offline][模型修复] _NeedPrefabResource=false (走 bundle) #" + c + ": '" + bundleName +
                              "' → '" + n + "' (asset='" + assetName + "')");
                return false;                                   // ★ 跳过原方法 (否则会被覆盖回 true)
            }
            catch { return true; }
        }

        // ★修复 2★ BundleList.IsBundle(string,string): 清单里有 → 强制 true (=判定为 bundle)
        public static bool IsBundle2Prefix(string bundleName, string assetName, ref bool __result)
        {
            _lastBundleName = bundleName; _lastAssetName = assetName;
            try
            {
                if (string.IsNullOrEmpty(assetName)) return true;
                if (!IsPrefabLikeAsset(assetName)) return true;
                string n = LookupBundleName(bundleName);
                if (n == null) return true;
                __result = true;
                int c = _gateBundle++;
                if (c < 20 || c % 200 == 0)
                    Debug.Log("[CFZ-Offline][模型修复] IsBundle=true #" + c + " (asset='" + assetName +
                              "' bundle='" + bundleName + "')");
                return false;
            }
            catch { return true; }
        }

        public static void IsBundle2Postfix(bool __result)
        {
            try
            {
                if (_gateProbe++ >= 10) return;                 // 只留前 10 条样本
                string a = _lastAssetName;
                if (!IsPrefabLikeAsset(a)) return;
                Debug.Log("[CFZ-Offline][探针][IsBundle] name='" + _lastBundleName + "' asset='" + a + "' → " + __result);
            }
            catch { }
        }

        static string _lastBundleName = null;
        static string _lastAssetName = null;

        // ==================== ★大厅角色顶替★ ====================================================
        //
        //  现象: [bundle] 加载结束 bundle='FPS_M_SWAT'
        //          asset='Prefabs/Characters/LobbyQV/LobbyCharacters/Default/FPS_M_SWAT_BL_LOBBY_CHARACTER' → ★null★
        //
        //  BundleCacheEntry.GetAsset 的现场 (BundleCacheEntry.cs:48-89, 精确路径匹配, 无模糊):
        //     Find Path Name = Assets/Game Assets/_ResourcesToAssetBundle/Prefabs/Characters/LobbyQV/
        //                      LobbyCharacters/Default/FPS_M_SWAT_BL_LOBBY_CHARACTER.prefab
        //     Bundle.GetAllAssetNames() = 12 个, 其中:
        //        prefabs/characters/qv/character/default/fps_m_swat_bl_ingame_character.prefab     ← 有
        //        prefabs/characters/qv/equipment/default/fps_m_swat_bl_equip_helmet.prefab         ← 有
        //        prefabs/characters/lobbyqv/lobbyequipment/default/fps_m_swat_bl_lobbyequip_helmet.prefab ← 有
        //        prefabs/characters/pvarm/default/fps_m_swat_bl_arm.prefab                          ← 有
        //        ★ 没有 lobbyqv/lobbycharacters/... ★
        //
        //  → 不是"包名解析错", 是**大厅角色本体这个资产本地完全不存在**:
        //      · Bundle/AssetBundleList.json 的 "Character_Lobby" 分类 = 0 条, 全清单 lobbycharacter 命中 0
        //      · 本地只有散装源资产: Bundle/Character/新建文件夹/GameObject/FPS_*_LOBBY_CHARACTER.fbx
        //        (FBX 是编辑器格式, 运行时不能加载; 也不能靠 Resources 救)
        //
        //  → 可行修法: 拿同角色的 in-game 本体顶替 —— 同一个 bundle 里就有:
        //      Prefabs/Characters/LobbyQV/LobbyCharacters/Default/FPS_M_SWAT_BL_LOBBY_CHARACTER
        //   →  Prefabs/Characters/QV/Character/Default/FPS_M_SWAT_BL_INGAME_CHARACTER
        //    (BundleLoadJob.AssetName 是 { get; protected set; }, 用反射写; 只碰大厅角色, 其余一律不碰)
        //
        //  在哪里改: BundleLoadJob 的构造函数 (BundleLoadJob.cs:45) —— 它同时拿到 bundleName/assetName,
        //            而后续 _LoadFromCache/_LoadFromFile 都用 job.AssetName 去取资产, 改这里一处即全程生效。

        static int _lobbySubst = 0;

        public static void BundleJobCtorPostfix(object __instance)
        {
            try
            {
                object job = __instance;
                if (job == null) return;
                string an = Member(job, "AssetName") as string;
                if (string.IsNullOrEmpty(an)) return;
                if (an.IndexOf("LobbyQV/LobbyCharacters", StringComparison.OrdinalIgnoreCase) < 0) return;

                int slash = an.LastIndexOf('/');
                string file = (slash >= 0) ? an.Substring(slash + 1) : an;
                string sub = file.Replace("_LOBBY_CHARACTER", "_INGAME_CHARACTER");
                if (sub == file) return;                        // 名字里没有 _LOBBY_CHARACTER → 不碰

                string nn = "Prefabs/Characters/QV/Character/Default/" + sub;
                bool ok; SetMemberQuiet(job, "AssetName", nn, out ok);
                if (ok && _lobbySubst++ < 8)
                    Debug.LogWarning("[CFZ-Offline][大厅角色] 大厅本体本地缺失 → 顶替为 in-game 本体: '" +
                                     an + "' → '" + nn + "'");
            }
            catch { }
        }

        // ==================== ★救援★ 把玩家从 y=-1000 拉回出生点 ====================

        static int _forceRespawnCount = 0;

        static bool SetMemberQuiet(object o, string name, object v, out bool ok)
        {
            ok = false;
            if (o == null) return false;
            try
            {
                var t = o.GetType();
                var f = t.GetField(name, F2);
                if (f != null) { f.SetValue(o, v); ok = true; return true; }
                var pr = t.GetProperty(name, F2);
                if (pr != null && pr.CanWrite) { pr.SetValue(o, v, null); ok = true; return true; }
            }
            catch { }
            return false;
        }

        // 走官方 RecvRespawn 路径: 为每个玩家构造独立的 EventParam_PlayerRespawn
        static void ForceRespawnAll()
        {
            try
            {
                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                if (pm == null) { Debug.LogWarning("[CFZ-Offline][救援] PlayerManager=null"); return; }

                var lp = Refl(pm, "_listPlayers") as System.Collections.IList;
                if (lp == null || lp.Count == 0) { Debug.LogWarning("[CFZ-Offline][救援] _listPlayers 为空"); return; }

                var mRecv = pm.GetType().GetMethod("RecvRespawn", F2);
                if (mRecv == null) { Debug.LogWarning("[CFZ-Offline][救援] RecvRespawn 未找到"); return; }
                var tParam = mRecv.GetParameters()[0].ParameterType;

                // 出生点: 从上一轮捕获的 respawn 参数里取 RespawnPos
                object spawnPos = null;
                for (int i = 0; i < _respParams.Count; i++)
                {
                    object v = Member(_respParams[i], "RespawnPos");
                    if (v != null) { spawnPos = v; break; }
                }
                if (spawnPos == null) { Debug.LogWarning("[CFZ-Offline][救援] 捕获不到 RespawnPos (捕获数=" + _respParams.Count + ")"); return; }

                int ok = 0;
                for (int i = 0; i < lp.Count; i++)
                {
                    object p = lp[i];
                    if (p == null) continue;
                    try
                    {
                        object data = Member(p, "Data");
                        object prm = Activator.CreateInstance(tParam);
                        bool a, b, c, d, e, f;
                        SetMemberQuiet(prm, "RespawnPlayerID", Member(data, "PlayerID"), out a);
                        SetMemberQuiet(prm, "TeamID", Member(data, "TeamType"), out b);
                        SetMemberQuiet(prm, "MaxHP", 100, out c);
                        SetMemberQuiet(prm, "HP", 100, out d);
                        SetMemberQuiet(prm, "RespawnPointIndex", 0, out e);
                        SetMemberQuiet(prm, "Is_Spawn", true, out f);
                        bool g;
                        SetMemberQuiet(prm, "RespawnPos", spawnPos, out g);

                        mRecv.Invoke(pm, new object[] { prm });

                        // 双保险: 直接把位置摆过去
                        var comp = p as UnityEngine.Component;
                        if (comp != null && spawnPos is Vector3)
                        {
                            var pos3 = (Vector3)spawnPos;
                            comp.transform.position = pos3;
                            var phy = Member(p, "PhysicalObject");
                            if (phy != null) { bool phyOk; SetMemberQuiet(phy, "Position", pos3, out phyOk); }
                            Debug.Log("[CFZ-Offline][救援]  P" + i + " '" + comp.gameObject.name + "' → " + pos3.ToString("F1") +
                                      " | 字段 ID=" + a + " Team=" + b + " MaxHP=" + c + " HP=" + d + " Idx=" + e + " IsSpawn=" + f + " Pos=" + g);
                        }
                        ok++;
                    }
                    catch (Exception ex) { Debug.LogError("[CFZ-Offline][救援]  P" + i + " 失败: " + ex.Message); }
                }
                Debug.LogWarning("[CFZ-Offline][救援] 已对 " + ok + "/" + lp.Count + " 个玩家执行重生, 出生点=" + spawnPos);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][救援] 异常: " + e.Message); }
        }

        static void InMapWatchdog()
        {
            try
            {
                if (!_wantIngame) return;                    // F9 之前(大厅)不观测, 避免耗尽配额
                if (_mapWatchCount >= 20) return;
                if (Time.realtimeSinceStartup - _lastWatch2 < 2f) return;
                _lastWatch2 = Time.realtimeSinceStartup;
                _mapWatchCount++;

                object wsVal = null;
                try
                {
                    var f = typeof(InGameModeBase).GetField("WorldState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) wsVal = f.GetValue(null);
                }
                catch { }

                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                object mp = pm != null ? Member(pm, "MyPlayer") : null;
                var mt = mp as UnityEngine.Component;

                string s = "[CFZ-Offline][地图] #" + _mapWatchCount + " WorldState=" + wsVal + " timeScale=" + Time.timeScale.ToString("0.00");
                if (mt != null)
                {
                    s += " 玩家'" + mt.gameObject.name + "'=" + mt.transform.position.ToString("F1") + " 激活=" + mt.gameObject.activeInHierarchy;
                    object phy = Member(mp, "PhysicalObject");
                    if (phy != null) s += " 物理=" + Member(phy, "Position");
                    object mc = Member(mp, "MovementController");
                    if (mc is UnityEngine.Behaviour) s += " MoveCtl.enabled=" + ((UnityEngine.Behaviour)mc).enabled;
                }
                else s += " MyPlayer=null";
                var cam = Camera.main;
                if (cam != null)
                {
                    var p = cam.transform.parent;
                    s += " | 主相机=" + cam.transform.position.ToString("F1") + " 父=" + (p != null ? p.name : "无") + " enabled=" + cam.enabled;
                }
                else s += " | 主相机=NULL";

                // ★ 相机全景 + 角色可见性 (判断"画面无变化"到底是没相机还是没模型)
                try
                {
                    var cams = Camera.allCameras;
                    s += " | 相机数=" + cams.Length;
                    for (int i = 0; i < cams.Length && i < 4; i++)
                    {
                        var c = cams[i];
                        s += " [" + i + "]" + c.name + "@" + c.transform.position.ToString("F1") +
                             " 父=" + (c.transform.parent != null ? c.transform.parent.name : "无") +
                             " depth=" + c.depth.ToString("0") + " mask=" + c.cullingMask + " on=" + c.enabled;
                    }
                    object sc = null;
                    try { sc = CFW.InGame.GameCamera.CameraUtility.singleton.SceneCamera; } catch { }
                    var scc = sc as UnityEngine.Component;
                    if (scc != null)
                    {
                        var sccam = scc.GetComponent<UnityEngine.Camera>();
                        s += " || SceneCamera@" + scc.transform.position.ToString("F1") +
                             " 自身相机=" + (sccam != null ? (sccam.name + " on=" + sccam.enabled + " depth=" + sccam.depth) : "无") +
                             " 子相机=" + scc.GetComponentsInChildren<UnityEngine.Camera>(true).Length;
                    }
                    else s += " || SceneCamera=NULL";
                    if (mt != null)
                    {
                        s += " || 角色Renderer=" + mt.GetComponentsInChildren<UnityEngine.Renderer>(true).Length +
                             " Skinned=" + mt.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true).Length +
                             " 子物体=" + mt.transform.childCount;
                        object mdl = Member(mp, "Model");
                        s += " Model=" + (mdl != null ? "有" : "★无★");
                        object shot = Member(mp, "Shot");
                        s += " Shot=" + (shot != null ? "有" : "★无★");
                    }

                    // ★★★ 旋转闸门诊断 (PlayerRotation.OnInit 第245-265行 / LateUpdate 第146-170行)
                    object rotD = Member(mp, "Rotation");
                    if (rotD != null)
                    {
                        object isMyD = Member(rotD, "IsMyPlayer");
                        object enD = Member(rotD, "isEnable");
                        object spineD = Member(rotD, "tSpine");
                        object neckD = Member(rotD, "tNeck");
                        object cr = Member(rotD, "CharacterRotation");
                        object gr = Member(rotD, "GunRotation");
                        string crY = "-", grX = "-";
                        if (cr is UnityEngine.Quaternion) crY = ((UnityEngine.Quaternion)cr).eulerAngles.y.ToString("F1");
                        if (gr is UnityEngine.Quaternion) grX = ((UnityEngine.Quaternion)gr).eulerAngles.x.ToString("F1");
                        s += " || Rotation{IsMy=" + (isMyD ?? "?") + " enable=" + (enD ?? "?") +
                             " tSpine=" + (spineD != null ? "有" : "★无★") +
                             " tNeck=" + (neckD != null ? "有" : "★无★") +
                             " CharYaw=" + crY + " GunPitch=" + grX +
                             " bodyYaw=" + (Member(rotD, "m_fBodyYaw") ?? "?") +
                             " gunYaw=" + (Member(rotD, "m_fGunYaw") ?? "?") + "}";
                    }
                    if (scc != null)
                    {
                        s += " 相机Yaw=" + scc.transform.eulerAngles.y.ToString("F1");
                        var fpv = scc.transform.Find("FPVCamera");
                        if (fpv != null)
                        {
                            var fpvc = fpv.GetComponent<UnityEngine.Camera>();
                            s += " FPVCamera=" + (fpvc != null ? ((fpvc.enabled ? "on" : "off") + "/mask=" + fpvc.cullingMask) : "无组件");
                        }
                    }
                    try
                    {
                        // AudioListener 在 UnityEngine.AudioModule (未引用) → 按名字取类型 + 非泛型 FindObjectsOfType
                        var alType = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                        if (alType != null)
                        {
                            var arr = UnityEngine.Object.FindObjectsOfType(alType);
                            s += " || AudioListener=" + arr.Length;
                            if (arr.Length > 0)
                            {
                                var c0 = arr[0] as UnityEngine.Component;
                                if (c0 != null)
                                    s += "(enabled=" + (c0 is UnityEngine.Behaviour ? ((UnityEngine.Behaviour)c0).enabled.ToString() : "?") +
                                         " 父=" + (c0.transform.parent != null ? c0.transform.parent.name : "无") + ")";
                            }
                        }
                        else s += " || AudioListener类型未找到";
                    }
                    catch { }
                    try
                    {
                        var pmi = CFW.InGame.Player.PlayerManager.Instance;
                        if (pmi != null)
                        {
                            var cnt = pmi.GetType().GetMethod("GetPlayerCount", F2);
                            s += " || 玩家数=" + (cnt != null ? cnt.Invoke(pmi, null).ToString() : "?");
                        }
                    }
                    catch { }
                    s += " 鼠标=(" + UnityEngine.Input.GetAxisRaw("Mouse X").ToString("F2") + "," +
                         UnityEngine.Input.GetAxisRaw("Mouse Y").ToString("F2") + ")";
                }
                catch (Exception ec) { s += " | 相机探针异常:" + ec.Message; }

                Debug.Log(s);

                // 玩家当前处于的所有状态 (自动枚举 PlayerStateType)
                if (mp != null)
                {
                    object st = Member(mp, "State");
                    var m = st != null ? st.GetType().GetMethod("IsStateActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    if (m != null)
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                        {
                            var names = Enum.GetNames(ps[0].ParameterType);
                            var on = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < names.Length; i++)
                            {
                                try
                                {
                                    object v = Enum.Parse(ps[0].ParameterType, names[i]);
                                    if ((bool)m.Invoke(st, new object[] { v })) on.Add(names[i]);
                                }
                                catch { }
                            }
                            Debug.Log("[CFZ-Offline][地图]   玩家状态: " + (on.Count > 0 ? string.Join(", ", on.ToArray()) : "(无)"));

                            // 状态机空转 → 踢一脚 Idle (否则没有任何状态在驱动移动)
                            if (on.Count == 0)
                            {
                                var mChg = st.GetType().GetMethod("OnChangeState", F2);
                                if (mChg != null)
                                {
                                    object idle = Enum.Parse(ps[0].ParameterType, "Idle");
                                    object r = mChg.Invoke(st, new object[] { idle, null });
                                    Debug.LogWarning("[CFZ-Offline][地图]   状态机空转 → 强制 OnChangeState(Idle) 返回=" + r);
                                }
                            }
                        }
                    }
                }

                // ==================== 输入闸门修复 + 探针 ====================
                KickInputManager();
                try
                {
                    string inp = "Unity.W按下=" + UnityEngine.Input.GetKey(UnityEngine.KeyCode.W);
                    object mc2 = mp != null ? Member(mp, "MovementController") : null;
                    if (mc2 != null)
                    {
                        var gm = mc2.GetType().GetMethod("GetMoveDirection", F2);
                        if (gm != null) { try { inp += " MoveDir=" + gm.Invoke(mc2, null); } catch { } }
                    }
                    if (mp != null) inp += " IsEnterGame=" + Member(mp, "IsEnterGame");
                    Debug.Log("[CFZ-Offline][地图]  " + inp);
                }
                catch (Exception ex) { Debug.LogError("[CFZ-Offline][地图] 输入探针异常: " + ex.Message); }

                // ★ 玩家被留在 Vector3.down * 1000 (地图下方) → 走官方 RecvRespawn 拉回出生点
                if (mt != null && _forceRespawnCount < 3 && mt.transform.position.y < -100f)
                {
                    _forceRespawnCount++;
                    Debug.LogWarning("[CFZ-Offline][地图] 玩家在 y=" + mt.transform.position.y.ToString("F1") +
                                     " (地图下方) → 第 " + _forceRespawnCount + " 次救援");
                    ForceRespawnAll();
                    Debug.Log("[CFZ-Offline][地图] 救援后玩家位置=" + mt.transform.position.ToString("F1"));
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][地图] 异常: " + e.Message); }
        }

        public static bool LoadingUIOnEventPrefix(CFW.Framework.GameEvent gameEvent, CFW.Framework.IGameEventParam param)
        {
#if DEBUG
            try { string n = gameEvent.ToString(); if (IsWatchedEvent(n)) Debug.Log("[CFZ-Offline] ◆◆ LoadingUI 收到 " + n); }
            catch { }
#endif
            return true;
        }

        public static bool SetModeInfoPrefix(string PlayerID, long exp)
        {
            try { Debug.Log("[CFZ-Offline] ★ SetModeInfo 被调用 PlayerID='" + PlayerID + "' exp=" + exp); } catch { }
            // ★★ 数量/杀敌数必须在原方法**体内**读走之前设好 —— 它紧接着就按 NPCCount 造 bot (:93/116), :26 抄 KillCount
            try { Loadout.ArmRules(); } catch { }
            return true;
        }

        public static Exception SetModeInfoFinalizer(Exception __exception)
        {
            try { if (__exception != null) Debug.LogError("[CFZ-Offline] ★★★ SetModeInfo 抛异常: " + __exception); } catch { }
            return __exception;
        }

        // ★ AI_Tutorial_Loading.SetModeInfo 的 postfix = 角色/武器改写点 (见 Loadout 类顶部注释)
        public static void SetModeInfoPostfix()
        {
            try { Loadout.Apply(); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline][角色武器] SetModeInfo postfix 异常: " + e); }
            // ★换图已移除★ 不再改写 Param_LoadingStart.MapIndex (恒为游戏默认 1)
        }

        // ==================== ★敌人数量★ ==========================================================
        //
        //  【数量】[敌人] 数量 → InGameMode_AI_Tutorial.NPCCount (Loadout.ArmRules, 在 SetModeInfo 之前设)
        //    ★上限 7★  AI_Tutorial_Loading.cs:17  BotMaxName = 7
        //               AI_Tutorial_Loading.cs:97  num3 = Random.Range(10001, 10001 + BotMaxName)
        //               且要求 7 个名字互不重复 → NPCCount>7 时 while(true) 永远凑不齐 → SetModeInfo 直接卡死。
        //               (所以 ini 里超过 7 会被夹到 7 并打警告)
        //    ★敌人永远和我对立★ (Loadout.Apply: 我 BL → AI GR / 我 GR → AI BL), 不做队友。
        //
        //  【杀敌数】[敌人] 杀敌数 → InGameMode_AI_Tutorial.KillCount (同一个 ArmRules)
        //      AI_Tutorial_Loading.cs:26  Param_LoadingStart.KillCount = InGameMode_AI_Tutorial.KillCount
        //      InGame.cs:248              InGameModeBase.Create(..., param.KillCount, ...)
        //      InGameModeBase.cs:99       GoalMaxCount = InGameUtil.CaculateGoalType(killCount, maxRoundCount, ...)
        //      InGameMode_AI_Tutorial.cs:74/78  两边分数谁先到 GoalMaxCount 谁赢 (HUD 用 InGame.cs:272
        //                               的 InGameUI_SetGoalCount 显示 X/目标)
        //    ★最小 2★ InGameUtil.CaculateGoalType: roundCount>1 → 按回合数; killCount>1 → 按击杀数;
        //      否则 GoalType.Time 且返回 **-1** → 而 OnUpdateRoundScore 判的是 RoundScore >= GoalMaxCount
        //      → 0 >= -1 恒真 → 一进游戏回合立刻结算。所以 0/1 会被夹到 2。
        //    超过 600 也没关系: 回合时间到 (AI_Tutorial_Loading.RoundTime = 600 秒) 就按当时比分结算。
        //
        //  【刷点】出生/复活点是用 SlotIndex 去地图里查的:
        //      InGameMode_AI_Tutorial.cs:189  GetRespawnPoint(ETeamID.GR, SlotIndex)   ← 硬编码 GR (刷 bot)
        //      InGameMode_AI_Tutorial.cs:175  GetRespawnPoint(ETeamID.BL, 0)            ← 硬编码 BL (刷我)
        //      InGameMode_AI_Tutorial.cs:156  GetRespawnPoint(ETeamID.BL, SlotIndex)    ← 硬编码 BL (我死亡复活)
        //    InGameData_RespawnPoint.cs:116-131: 按 RespawnOrder 找不到只打一行 LogError 就 return null,
        //    而 _TutorialStart (:189) 紧接着就 respawnData2.Pos → ★NullReferenceException★
        //    → 协程当场死掉 → OnRoundStarting() 永不执行 → 卡在加载画面 / 不能动 / 不能开枪。
        //    数量>4 时 slotIndex 9/11/13 地图里没有就会踩到 (我选 GR 时那条硬编码 BL 同理)。
        //    所以这里兜底: 返回**该队伍里真实存在**的一个刷点 (按序号均匀分散), 让流程继续走。
        static int _spawnFixLog = 0;

        // ---- ★刷点兜底★ GetRespawnPoint(ETeamID, int) ----
        public static void GetRespawnPointPostfix(CFW.GameSystem.InGameData_RespawnPoint __instance,
            ETeamID teamID, int respawnPointIndex, ref CFW.GameSystem.RespawnPointEntry __result)
        {
            if (__result == null)
            {
                try { __result = FallbackSpawnPoint(__instance, teamID, respawnPointIndex); }
                catch (Exception e) { if (_spawnFixLog++ < 5) Debug.LogWarning("[CFZ-Offline][刷点] 兜底失败: " + e.Message); }
            }
            // ★换图已移除★ 不再对刷点做贴地/"我附近"改写, 一律用游戏原生刷点
        }

        // ---- ★刷点兜底★ GetRespawnPoint(ETeamID) ----
        public static void GetRespawnPoint1Postfix(CFW.GameSystem.InGameData_RespawnPoint __instance,
            ETeamID teamID, ref CFW.GameSystem.RespawnPointEntry __result)
        {
            if (__result == null)
            {
                try { __result = FallbackSpawnPoint(__instance, teamID, 0); } catch { }
            }
            // ★换图已移除★ 同上
        }

        // ---- ★刷点修正 (跟"换图"配套)★ ----
        //   地图 LevelData 里的刷点是"别人地图数据里的一个坐标", 换到别的图上经常没人验证过它能不能站人:
        //   实测 Temple 的 GR(敌人)刷点 (35~41, 0, 22~27) —— 往下打不到地面、往上也打不到 → 那地方是空的,
        //   bot 出生后一路掉到 -12000m, 玩家看到的就是"地图上没有敌人"。
        //   这里出生前逐个点验证:
        //     ① 脚下有地面 → 用 (稍微陷进去就抬上来) ② 整个埋在几何体里 → 往上钻出来
        //     ③ 上下都打不到(空的) / 悬在半空 → 改用玩家出生点旁边的点 (日志会说明)
        //   另外 [地图] 敌人出生 = 我附近 时, 直接全部用玩家出生点旁的一圈 (大地图两队刷点常相隔 90m+)。
        static int _groundFixLog = 0;
        static UnityEngine.Vector3 _blSpawn;                     // 玩家(BL, order 0)的刷点 = "我附近" 的圆心
        static bool _blSpawnOk = false;
        static UnityEngine.Vector3 _grSpawn;                     // 一个"验证过能用"的地图 GR(敌人)刷点 → 掉落修复回这儿
        static bool _grSpawnOk = false;
        static int _badSpawnTalk = 0;

        // ★ bot 的"地面"只在 NaviMesh 层 ★ —— NpcPathFinder.Start 里写死的:
        //     groundMask = 1 << CFPhysics.Layer_NaviMesh;
        //   所以射线不带这个 mask 的话, 可能把 bot 摆到"看着有地面、其实不是它认的层"的地方 → 悬空/掉落。
        static int NaviMask
        {
            get
            {
                try
                {
                    int l = CFW.Physics.CFPhysics.Layer_NaviMesh;
                    return (l >= 0 && l < 32) ? (1 << l) : ~0;
                }
                catch { return ~0; }
            }
        }

        // bot 认的"地面"就是这一层 (NpcPathFinder.Start: groundMask = 1 << CFPhysics.Layer_NaviMesh)
        //   ★只认这一层, 不做"任意层"兜底★ —— 这张图上有 y≈21 的装饰/顶部碰撞体, 用任意层射线会被它骗到,
        //   bot 摆上去就掉 (日志里 20 多条 "放回 (-80, 21, …)" 全是这个假地面, 放了又掉, 反复循环)。
        static bool NavRay(UnityEngine.Vector3 from, UnityEngine.Vector3 dir, float dist, out UnityEngine.RaycastHit hit)
        {
            hit = default(UnityEngine.RaycastHit);
            int nm = NaviMask;
            return (nm != ~0) && UnityEngine.Physics.Raycast(from, dir, out hit, dist, nm);
        }

        static void FixSpawnPoint(ref CFW.GameSystem.RespawnPointEntry p, ETeamID team, int idx)
        {
            if (p == null) return;
            try
            {
                if (team == ETeamID.BL && idx == 0 && p.Pos != UnityEngine.Vector3.zero)
                { _blSpawn = p.Pos; _blSpawnOk = true; }

                if (team != ETeamID.GR) return;                       // 只动敌人的刷点, 玩家自己的保持游戏原样

                if (Loadout.MapSpawnNearMe && _blSpawnOk)             // ★ 我附近
                {
                    UnityEngine.Vector3 np0 = RingSpot(idx);
                    if (_groundFixLog++ < 20)
                        Debug.LogWarning("[CFZ-Offline][刷点] 敌人出生=我附近: GR 刷点#" + idx + " → " + np0.ToString("F0") +
                                         " (玩家出生点 " + _blSpawn.ToString("F0") + " 旁)");
                    p.Pos = np0;
                    return;
                }

                UnityEngine.Vector3 fixedPos; string why;            // ★ 地图刷点: 就按地图给的坐标, 只验证"脚下有没有地面"
                if (SpawnUsable(p.Pos, out fixedPos, out why))
                {
                    if (!_grSpawnOk) { _grSpawn = fixedPos; _grSpawnOk = true; }   // 记一个验证过的敌人刷点(掉落修复用)
                    if (fixedPos != p.Pos && _groundFixLog++ < 20)
                        Debug.LogWarning("[CFZ-Offline][刷点] GR 刷点#" + idx + " " + p.Pos.ToString("F1") +
                                         " → " + fixedPos.ToString("F1") + " (" + why + ")");
                    p.Pos = fixedPos;
                    return;
                }
                if (_blSpawnOk)                                       // 这个点不能用 → 借用玩家出生点旁的
                {
                    UnityEngine.Vector3 np1 = RingSpot(idx);
                    if (!_grSpawnOk) { _grSpawn = np1; _grSpawnOk = true; }
                    if (_badSpawnTalk++ < 8)
                        Debug.LogWarning("[CFZ-Offline][刷点] 地图的 GR 刷点#" + idx + " " + p.Pos.ToString("F1") +
                                         " 不能用(" + why + ") → 改用玩家出生点旁的 " + np1.ToString("F0") +
                                         " (想每次都这样设 [地图] 敌人出生 = 我附近)");
                    p.Pos = np1;
                }
            }
            catch (Exception e) { if (_groundFixLog++ < 5) Debug.LogWarning("[CFZ-Offline][刷点] 修正失败: " + e.Message); }
        }

        // 这个刷点能不能用: 脚下得有 bot 认的地面 (NaviMesh 层)。
        //   ★不再要求"在 A* 导航图上"★ —— 实测 Temple 的 A* 图整体在 y≈-28 (比场景低 28m),
        //   全图都"不在图上", 按那个判据会把地图刷点全部否掉 → bot 全跑到玩家身边"凭空刷出"。
        static bool SpawnUsable(UnityEngine.Vector3 pos, out UnityEngine.Vector3 fixedPos, out string why)
        {
            string how;
            bool ok = TryGroundSpot(pos, out fixedPos, out how);
            why = ok ? how : "脚下 5m 内没有 NaviMesh 层地面(这个位置是空的)";
            return ok;
        }

        // 这个点能不能站人 (只看 bot 认的 NaviMesh 层)
        //   ① 从点位上方 0.5m 往下打 5m: 打到地面 → 可用
        //      · 地面比点位高 0.4m 以上(埋在里头) → 抬到地面   · 比点位低 2m 以上(悬空) → 落到地面   · 否则原地
        //      ★射线必须从"脚边"起★: 之前从上方 3m 起, 结果打到头顶 2.5m 处的棚/装饰面, 把 bot 抬到棚上 (Temple 实测)
        //   ② 打不到 → 可能整个埋在几何体里: 往上打, 打到就"钻出来"
        //   ③ 还不行 → 无效 (调用方改用玩家出生点旁的落脚点)
        static bool TryGroundSpot(UnityEngine.Vector3 pos, out UnityEngine.Vector3 fixedPos, out string how)
        {
            fixedPos = pos; how = "原地";
            UnityEngine.RaycastHit hit;
            if (NavRay(new UnityEngine.Vector3(pos.x, pos.y + 0.5f, pos.z), UnityEngine.Vector3.down, 5f, out hit))
            {
                float dy = hit.point.y - pos.y;
                if (dy > 0.4f || dy < -2f)
                {
                    fixedPos = new UnityEngine.Vector3(pos.x, hit.point.y + 0.2f, pos.z);
                    how = (dy > 0.4f ? "抬出地面 " : "落地 ") + pos.y.ToString("F1") + "→" + fixedPos.y.ToString("F1");
                }
                return true;
            }
            if (NavRay(pos, UnityEngine.Vector3.up, 80f, out hit))        // 埋在里面 → 钻出来
            {
                fixedPos = new UnityEngine.Vector3(pos.x, hit.point.y + 0.6f, pos.z);
                how = "钻出地面 " + pos.y.ToString("F1") + "→" + fixedPos.y.ToString("F1");
                return true;
            }
            return false;
        }

        // 玩家出生点旁找一个 bot 真能站、真能走过去的点: 26~41m 一圈, 逐个方向/半径试
        //   ★优先直接用 A* 图节点★ (bot 的移动完全靠这张图) → 站上去就能自己巡逻
        static UnityEngine.Vector3 RingSpot(int idx)
        {
            if (!_blSpawnOk) return _blSpawn;
            for (int k = 0; k < 8; k++)
            {
                int n = idx + k * 3;                                  // 换个角度/半径再试
                float ang = (n % 16) * 22.5f * UnityEngine.Mathf.Deg2Rad;
                float r = 26f + (n % 4) * 5f;
                UnityEngine.Vector3 c = new UnityEngine.Vector3(
                    _blSpawn.x + UnityEngine.Mathf.Cos(ang) * r, _blSpawn.y, _blSpawn.z + UnityEngine.Mathf.Sin(ang) * r);
                UnityEngine.RaycastHit h;
                // ★从我出生点上方 6m 往下打(不能用 40m: 那张图 y≈21 有装饰/顶部碰撞, 会被打到)★
                //   打到 NaviMesh 层地面, 且别掉太深(>15m 说明是个坑) → 就落在那儿
                if (NavRay(new UnityEngine.Vector3(c.x, _blSpawn.y + 6f, c.z), UnityEngine.Vector3.down, 40f, out h)
                    && h.point.y > _blSpawn.y - 15f)
                    return new UnityEngine.Vector3(c.x, h.point.y + 0.6f, c.z);
            }
            return new UnityEngine.Vector3(_blSpawn.x + 1.5f + idx * 0.5f, _blSpawn.y + 0.2f, _blSpawn.z);
        }

        static CFW.GameSystem.RespawnPointEntry FallbackSpawnPoint(object data, ETeamID teamID, int order)
        {
            var dic = Member(data, "respawnPoints") as System.Collections.IDictionary;
            if (dic == null || dic.Count == 0) return null;
            var list = AsPointList(dic, teamID);
            string src = teamID.ToString();
            if (list == null)                                   // 该队伍一个刷点都没有 → 借别队的
            {
                foreach (System.Collections.DictionaryEntry de in dic)
                {
                    var l = de.Value as System.Collections.IList;
                    if (l != null && l.Count > 0) { list = l; src = de.Key.ToString(); break; }
                }
            }
            if (list == null || list.Count == 0) return null;
            int i = Math.Abs(order) / 2 % list.Count;           // 按序号均匀分散, 别全挤一个点
            var point = list[i] as CFW.GameSystem.RespawnPointEntry;
            if (_spawnFixLog++ < 20)
                Debug.LogWarning("[CFZ-Offline][刷点] " + teamID + " 里没有 RespawnOrder=" + order + " 的刷点 → 借用 " + src +
                                 " 的第 " + i + " 个 (order=" + (point != null ? (Member(point, "RespawnOrder") + "") : "?") +
                                 "). 常见原因: 敌人数量超过地图刷点数, 或该队伍没配这个序号");
            return point;
        }

        // ==================== ★bot 掉出地图修复★ ====================
        //  FixSpawnPoint 是"出生前验证刷点"(治本), 这里是"掉下去之后捞回来"(保险):
        //  每秒巡检一次, y 掉到玩家脚下 25m 以下的 bot → 用游戏自己的复活流程摆回地面。
        //  ★上一版失败的原因★ 只写 transform 没用: Player 的位置每帧是从 PhysicalObject
        //  (CFPhysicalProperty) 同步的 (Player.OnRespawn: transform.localPosition = PhysicalObject.Position),
        //  所以写 transform 会被立刻拉回原处 → bot 照样一路掉 (日志里 "已放回" 的 y 还是负的)。
        static int _botFixFrame = 0;
        static int _botFixLog = 0;
        static int _botFixRound = 0;                             // 给"玩家旁的落脚点"轮换用

        public static void BotDropFix(object mp)
        {
            try
            {
                var pls = UnityEngine.Object.FindObjectsOfType<CFW.InGame.Player.Player>();
                if (pls == null || pls.Length == 0) return;
                UnityEngine.Vector3 myPos = (mp is UnityEngine.Component)
                    ? ((UnityEngine.Component)mp).transform.position : UnityEngine.Vector3.zero;
                bool haveMe = (myPos != UnityEngine.Vector3.zero);
                for (int i = 0; i < pls.Length; i++)
                {
                    var p = pls[i];
                    if (p == null) continue;
                    object d = Member(p, "Data");
                    if (d != null)
                    {
                        object isMy = Member(d, "IsMyPlayer");
                        if (isMy is bool && (bool)isMy) continue;                 // 只管敌人(bot), 玩家自己不动
                    }
                    UnityEngine.Vector3 pos = p.transform.position;
                    if (haveMe && pos.y >= myPos.y - 25f) continue;                // 正常高度 → 不动
                    if (!haveMe && pos.y >= -50f) continue;                        // 玩家还没出生 → 只捞已经掉很深的

                    UnityEngine.Vector3 target = FallbackSpot(pos, haveMe ? myPos : pos);
                    PutBot(p, target);
                    if (_botFixLog++ < 30)
                        Debug.LogWarning("[CFZ-Offline][敌人修复] " + p.name + " 掉出地图 (y=" + pos.y.ToString("F0") +
                                         ") → 已按游戏复活流程放回 " + target.ToString("F0"));
                }
            }
            catch (Exception e) { if (_botFixLog++ < 5) Debug.LogWarning("[CFZ-Offline][敌人修复] 异常: " + e.Message); }
        }

        // 掉下去的 bot 放哪儿: ① 它自己那根柱子上的地面(但别比参考点低太多) ② 玩家出生点旁一圈 ③ 玩家脚边
        static UnityEngine.Vector3 FallbackSpot(UnityEngine.Vector3 bad, UnityEngine.Vector3 refPos)
        {
            UnityEngine.RaycastHit h;
            // ① 最优先: 回"验证过的地图 GR 刷点" (bot 本来该在的地方), 稍微散开一点
            if (_grSpawnOk)
            {
                int n = ++_botFixRound;
                float ang = (n % 8) * 45f * UnityEngine.Mathf.Deg2Rad;
                UnityEngine.RaycastHit hg;
                UnityEngine.Vector3 c = new UnityEngine.Vector3(_grSpawn.x + UnityEngine.Mathf.Cos(ang) * 3f,
                                                               _grSpawn.y, _grSpawn.z + UnityEngine.Mathf.Sin(ang) * 3f);
                if (NavRay(new UnityEngine.Vector3(c.x, c.y + 6f, c.z), UnityEngine.Vector3.down, 30f, out hg)) return new UnityEngine.Vector3(c.x, hg.point.y + 0.6f, c.z);
                return _grSpawn;
            }
            // ② 它自己那根柱子上有地面? (低位起打, 25m 内; 找不到就算了)
            if (NavRay(new UnityEngine.Vector3(bad.x, refPos.y + 6f, bad.z), UnityEngine.Vector3.down, 25f, out h) && h.point.y > refPos.y - 25f)
                return new UnityEngine.Vector3(bad.x, h.point.y + 0.6f, bad.z);
            if (_blSpawnOk)
            {
                UnityEngine.Vector3 c = RingSpot(++_botFixRound);
                if (c.y > refPos.y - 25f) return c;
            }
            return new UnityEngine.Vector3(refPos.x + 2f, refPos.y + 1.2f, refPos.z);
        }

        // ★ 用游戏自己的复活流程摆位★
        //   PlayerManager.RecvRespawn(EventParam_PlayerRespawn) —— AI 教学模式下 PlayerManager.cs:755 那支会
        //   直接把 eventParam.RespawnPos 交给 Player.OnRespawn, 里面统一重置 Sync / MovementController /
        //   状态机 / 血量, 和正常复活一模一样 (顺手再写一遍 PhysicalObject 位置+速度清零做双保险)。
        static int _putBotLog = 0;
        static void PutBot(object p, UnityEngine.Vector3 pos)
        {
            try
            {
                object data = Member(p, "Data");
                var pm = UnityEngine.Object.FindObjectOfType<CFW.InGame.Player.PlayerManager>();
                var mRecv = (pm != null) ? pm.GetType().GetMethod("RecvRespawn", F2) : null;
                if (mRecv != null)
                {
                    object prm = Activator.CreateInstance(mRecv.GetParameters()[0].ParameterType);
                    bool a, b, c, d, e, f, g;
                    SetMemberQuiet(prm, "RespawnPlayerID", Member(data, "PlayerID"), out a);
                    SetMemberQuiet(prm, "TeamID", Member(data, "TeamType"), out b);
                    SetMemberQuiet(prm, "MaxHP", 100, out c);
                    SetMemberQuiet(prm, "HP", 100, out d);
                    SetMemberQuiet(prm, "RespawnPointIndex", Member(data, "SlotIndex"), out e);
                    SetMemberQuiet(prm, "RespawnPos", pos, out f);
                    SetMemberQuiet(prm, "Is_Spawn", true, out g);
                    mRecv.Invoke(pm, new object[] { prm });
                    if (_putBotLog++ < 8)
                        Debug.Log("[CFZ-Offline][敌人修复] RecvRespawn: ID=" + a + " Team=" + b + " Pos=" + f + " IsSpawn=" + g);
                }
                var phy = Member(p, "PhysicalObject");                    // 双保险: 物理体位置 + 速度清零
                if (phy != null)
                {
                    bool o1, o2;
                    SetMemberQuiet(phy, "Position", pos, out o1);
                    SetMemberQuiet(phy, "Velocity", UnityEngine.Vector3.zero, out o2);
                }
            }
            catch (Exception e) { if (_putBotLog++ < 8) Debug.LogWarning("[CFZ-Offline][敌人修复] PutBot 失败: " + e.Message); }
        }

        // ==================== ★巡逻诊断★ bot 为什么站着不动 ====================
        //  bot 的巡逻链路 (AI_TutorialPlayer / NpcPathFinder):
        //    OnRespawn(:97) / FireEnd(:153) → aiPath.FindNextTarget() → 随机挑一个 targetList 子点
        //    → AIPath 沿 A* 图走过去 → OnTargetReached() 再挑下一个 (循环)
        //  走不动只有四种可能: ①canMove=false(在开枪/被 Stop) ②target=null(没挑点, 永远不会动)
        //    ③hasPath=false(图上到不了目标) ④脚下不是 NaviMesh 层地面(它只认这一层)
        static UnityEngine.Vector3[] _lastBotPos = new UnityEngine.Vector3[8];
        static bool _haveBotPos = false;

        static float MovedDelta(int i, UnityEngine.Vector3 pos)
        {
            if (i < 0 || i >= _lastBotPos.Length) return 0f;
            float d = _haveBotPos ? UnityEngine.Vector3.Distance(pos, _lastBotPos[i]) : 0f;
            _lastBotPos[i] = pos;
            return d;
        }

        static string AiState(object p)
        {
            try
            {
                object ap = Member(p, "aiPath");                     // NpcPathFinder (AI_TutorialPlayer 的私有字段)
                if (ap == null) return "无 aiPath";
                string s = "";
                object cm = Member(ap, "canMove");
                s += (cm is bool && (bool)cm) ? "走" : "停";
                object hp = Member(ap, "hasPath");
                s += (hp is bool && (bool)hp) ? "/有路" : "/★无路★";
                object rp = Member(ap, "pathPending");
                if (rp is bool && (bool)rp) s += "/算路中";
                object t = Member(ap, "target");
                var tt = t as UnityEngine.Transform;
                s += " 目标=" + (tt == null ? "★null★" : tt.name + tt.position.ToString("F0"));
                return s;
            }
            catch (Exception e) { return "异常:" + e.Message; }
        }

        // bot 脚下有没有 NaviMesh 层地面 (NpcPathFinder.Start: groundMask = 1 << CFPhysics.Layer_NaviMesh)
        //   只认这一层: 那张图上 y≈21 有装饰/顶部碰撞, "任意层"射线会把它们当地面 → 诊断会被误导
        static bool HasNaviGround(UnityEngine.Vector3 pos)
        {
            try
            {
                UnityEngine.RaycastHit h;
                return NavRay(new UnityEngine.Vector3(pos.x, pos.y + 1f, pos.z), UnityEngine.Vector3.down, 4f, out h);
            }
            catch { return false; }
        }

        static string AStarInfo(UnityEngine.Vector3 pos)
        {
            try
            {
                var asp = AstarPath.active;
                if (asp == null) return "★AstarPath.active=null★";
                var inf = asp.GetNearest(pos, Pathfinding.NNConstraint.Default);
                if (inf.node == null) return "★没节点★";
                return "最近节点 " + UnityEngine.Vector3.Distance(pos, inf.position).ToString("F1") + "m " +
                       (inf.node.Walkable ? "可走" : "★不可走★");
            }
            catch (Exception e) { return "异常:" + e.Message; }
        }

        // ★巡逻兜底★ bot 站着不动最硬的一种可能: 它压根没有巡逻目标
        //   (AIPath.target == null → 不会移动; 正常由 OnRespawn/FireEnd 里的 FindNextTarget() 设置)
        //   这时替它调一次 FindNextTarget() (它会自己随机挑 targetList 里的点 + canMove=true + 切 Moving 状态)
        static int _patrolNudgeLog = 0;
        static int _patrolNudgeCount = 0;

        static void PatrolNudge(object p)
        {
            try
            {
                object dead = Member(p, "IsDeadState");
                if (dead is bool && (bool)dead) return;
                object ap = Member(p, "aiPath");
                if (ap == null) return;
                if (Member(ap, "target") != null) return;                 // 有目标 → 正常, 不管
                var comp = p as UnityEngine.Component;
                string nm = comp != null ? comp.name : "?";
                var list = Member(ap, "targetList") as System.Collections.IList;
                if (list == null || list.Count == 0)
                {
                    if (_patrolNudgeLog++ < 6)
                        Debug.LogWarning("[CFZ-Offline][巡逻] " + nm + " 的 targetList 是空的 → bot 永远不会走动" +
                                         " (NpcPathFinder.Start 里 GameObject.Find(\"targetList\") 没拿到?)");
                    return;
                }
                var m = ap.GetType().GetMethod("FindNextTarget", F2);
                if (m == null)
                {
                    if (_patrolNudgeLog++ < 6) Debug.LogWarning("[CFZ-Offline][巡逻] 找不到 FindNextTarget 方法");
                    return;
                }
                m.Invoke(ap, null);
                _patrolNudgeCount++;
                if (_patrolNudgeLog++ < 10)
                    Debug.LogWarning("[CFZ-Offline][巡逻] " + nm + " 的巡逻目标=null(没人给它挑点) → 已替它 FindNextTarget()" +
                                     " (第 " + _patrolNudgeCount + " 次)");
            }
            catch (Exception e)
            {
                if (_patrolNudgeLog++ < 6) Debug.LogWarning("[CFZ-Offline][巡逻] 替 bot 挑巡逻点失败: " + e.Message);
            }
        }

        static System.Collections.IList AsPointList(System.Collections.IDictionary dic, ETeamID t)
        {
            if (dic == null || !dic.Contains(t)) return null;
            var l = dic[t] as System.Collections.IList;
            return (l != null && l.Count > 0) ? l : null;
        }

        public static bool NetClientTutorialAIPrefix()
        {
            try { Debug.Log("[CFZ-Offline] ★ NetClient.OnEvent_Tutorial_AI_EnterComplete 被调用"); } catch { }
            return true;
        }

        // 我们自己的兜底: 直接调用游戏自带的 AI_Tutorial_Loading.SetModeInfo
        public static bool DriveSetModeInfo()
        {
            try
            {
                Debug.Log("[CFZ-Offline] >>> 兜底调用 AI_Tutorial_Loading.SetModeInfo(\"TutorialPlayer\", 0)");
                bool r = AI_Tutorial_Loading.SetModeInfo("TutorialPlayer", 0L);
                Debug.Log("[CFZ-Offline] >>> SetModeInfo 返回 " + r + ", IsNowLoading=" + CFW.UI.Loading.LoadingUI.IsNowLoading);
                return true;
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] >>> 兜底 SetModeInfo 异常: " + e); return false; }
        }

        // ==================== ★★★ 关键修复 ★★★ ====================

        // CameraUtility.SceneCamera 是「按需查找」的 property:
        //   public SceneCamera SceneCamera => !_sceneCamera ? (_sceneCamera = FindObjectOfType<SceneCamera>()) : _sceneCamera;
        // 而 SceneCamera 组件必须由 Camera_SetSceneCamera 事件加上去 (InGame.OnEvent_SetSceneCamera -> AddComponent)。
        // 该事件在地图场景 SetSceneCamera.Start() 里发一次, 若有竞态/无人接收则组件永远缺失
        // → PlayerManager._CreatePlayer 死等 SceneCamera == null → 加载进度永久卡在 66%。
        // 修法: 任何一次 get_SceneCamera 拿到 null 时, 我们直接把组件补上。
        public static void SceneCameraGetterPostfix(ref CFW.InGame.GameCamera.SceneCamera __result)
        {
            try
            {
                if (__result != null) return;
                var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                bool found = false;
                string lastName = "";
                for (int pass = 0; pass < 2 && !found; pass++)
                {
                    for (int i = 0; i < cams.Length && !found; i++)
                    {
                        if (cams[i] == null) continue;
                        string nm = cams[i].name;
                        if (pass == 0)
                        {
                            if (nm.IndexOf("SceneCamera", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        else
                        {
                            if (cams[i].tag != "MainCamera") continue;
                        }
                        lastName = nm;
                        var sc = cams[i].gameObject.GetComponent<CFW.InGame.GameCamera.SceneCamera>();
                        if (sc == null) sc = cams[i].gameObject.AddComponent<CFW.InGame.GameCamera.SceneCamera>();
                        if (sc != null)
                        {
                            __result = sc;
                            Debug.Log("[CFZ-Offline] ★★ 自动补上 SceneCamera 组件: GO='" + nm + "' tag=" + cams[i].tag);
                            found = true;
                        }
                    }
                }
                // 节流: 大厅里没有游戏相机, 这个 getter 每帧都被调 → 不限量会刷屏 (10 秒一条)
                if (!found && Time.realtimeSinceStartup - _scWarnAt > 10f)
                {
                    _scWarnAt = Time.realtimeSinceStartup;
                    Debug.LogWarning("[CFZ-Offline] SceneCamera 缺失且找不到候选相机 (相机总数=" + cams.Length + ", 最后候选='" + lastName + "') [10秒最多1条]");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] SceneCameraGetterPostfix 异常: " + e); }
        }

        static float _scWarnAt = -100f;                          // SceneCamera 缺失警告节流

        // 教程流程必须走 Loading_End 分支; NetClient.IsTutorial 因我们绕过 OnEvent_Tutorial_AI_EnterComplete 而是 false
        // → 会走 _LoadWaitingPlayers() 然后得不到 Loading_End → 加载界面永不关闭。这里强制走教程路径。
        public static bool NetClientLoadingCompletePrefix()
        {
            try
            {
                Debug.Log("[CFZ-Offline] ★ NetClient.OnEvent_LoadingComplete → 强制走教程路径 Loading_End");
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Loading_End, null);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] NetClientLoadingCompletePrefix 异常: " + e); }
            return false;
        }

        // _LoadFromFile: 诊断 + ★接管 Scene 分支 (原版 Replace('.', '\0') 会把场景名搞坏)★
        public static bool LoadFromFilePrefix(Common.System.BundleLoadJob job, ref bool __result)
        {
            try
            {
                if (job == null) return true;
                if (_loggedBundles.Add("LFF:" + job.BundleName))
                    Debug.Log("[CFZ-Offline] ★ _LoadFromFile('" + job.BundleName + "') 类型=" + job.BundleInfo.BundleType +
                              " 路径='" + job.DownloadPath + "/" + job.FileName + "'" +
                              " CreateRequest=" + (job.CreateRequest != null) + " AsyncOperation=" + (job.AsyncOperation != null));
                if (job.BundleInfo.BundleType == Common.System.AssetBundleType.Scene &&
                    job.AsyncOperation == null && job.CreateRequest != null && job.CreateRequest.isDone)
                {
                    var ab = job.CreateRequest.assetBundle;
                    if (ab != null)
                    {
                        var paths = ab.GetAllScenePaths();
                        if (paths != null && paths.Length > 0)
                        {
                            string sceneName = System.IO.Path.GetFileNameWithoutExtension(paths[0]);
                            Loadout.LoadedSceneName = sceneName;      // ★ 自检用: 真正加载的是哪张图
                            Debug.Log("[CFZ-Offline] ★★ 场景 bundle 接管加载: '" + paths[0] + "' -> SceneManager.LoadSceneAsync('" + sceneName + "')");
                            job.AsyncOperation = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(sceneName);
                            __result = false;
                            return false;
                        }
                        Debug.LogWarning("[CFZ-Offline] GetAllScenePaths 为空, 回退原逻辑");
                    }
                    else Debug.LogWarning("[CFZ-Offline] assetBundle 为 null, 回退原逻辑");
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] LoadFromFilePrefix 异常: " + e); }
            return true;
        }

        public static void RepairAndRestartLoading()
        {
            try
            {
                var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var lui = UnityEngine.Object.FindObjectOfType<CFW.UI.Loading.LoadingUI>();
                if (lui == null) { Debug.LogError("[CFZ-Offline] F11: LoadingUI 未找到"); return; }
                var t = lui.GetType();
                var fUISet = t.GetField("UISet", F2);
                object cur = fUISet != null ? fUISet.GetValue(lui) : null;
                Debug.Log("[CFZ-Offline] F11: 当前 LoadingUI.UISet = " + (cur == null ? "NULL" : cur.GetType().Name));
                if (cur == null && fUISet != null)
                {
                    string[] cands = { "UISet_Tutorial", "UISet_Solo", "UISet_Team" };
                    for (int i = 0; i < cands.Length; i++)
                    {
                        var f = t.GetField(cands[i], F2);
                        object v = f != null ? f.GetValue(lui) : null;
                        Debug.Log("[CFZ-Offline] F11:   " + cands[i] + " = " + (v == null ? "NULL" : v.GetType().Name));
                        if (v != null)
                        {
                            fUISet.SetValue(lui, v);
                            Debug.Log("[CFZ-Offline] F11:   >> 已把 UISet 指向 " + cands[i]);
                            break;
                        }
                    }
                }
                var m = t.GetMethod("OnEvent_LoadingStart", F2);
                if (m != null)
                {
                    m.Invoke(lui, new object[] { CFW.Framework.GameEvent.Loading_Start, CFW.Network.NetEvent.Param_LoadingStart });
                    Debug.Log("[CFZ-Offline] F11: >>> 已重新触发 LoadingUI._Loading");
                }
                else Debug.LogWarning("[CFZ-Offline] F11: OnEvent_LoadingStart 未找到");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] RepairAndRestartLoading 异常: " + e); }
        }

        public static void ProbeMapBundle()
        {
            try
            {
                string name = CFW.DataTable.DataTableManager.MapTable.GetSceneName(1);
                Debug.Log("[CFZ-Offline] F1 尝试加载地图 bundle '" + name + "'");
                var rl = Common.System.ResourceLoader.Instance;
                Debug.Log("[CFZ-Offline]   IsBundleListReady=" + rl.IsBundleListReady + ", IsUseAssetBundle=" + rl.IsUseAssetBundle);
                var job = rl.Load<UnityEngine.Object>(name, string.Empty, null);
                Debug.Log("[CFZ-Offline]   BundleLoadJob = " + (job != null ? job.GetType().FullName : "null"));
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ProbeMapBundle 异常: " + e); }
        }

        public static void ForceGameSceneLoadingComplete()
        {
            try
            {
                CFW.UI.Loading.LoadingUI.isGameSceneLoadingComplete = true;
                Debug.Log("[CFZ-Offline] >>> 强制 LoadingUI.isGameSceneLoadingComplete = true");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ForceGameSceneLoadingComplete 异常: " + e); }
        }

        public static void ForceGameModeInit()
        {
            try
            {
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.GameMode_Init, CFW.Network.NetEvent.Param_LoadingStart);
                Debug.Log("[CFZ-Offline] >>> 强制 GameEvent.GameMode_Init");
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ForceGameModeInit 异常: " + e); }
        }

        public static void ResendInGameUIOnLoad()
        {
            try
            {
                Debug.Log("[CFZ-Offline] >>> 重发 GameEvent.InGameUI_OnLoad (修复注册时序)");
                CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.InGameUI_OnLoad, CFW.Network.NetEvent.Param_LoadingStart);
                Debug.Log("[CFZ-Offline] >>> 重发完成, Manager!=null=" + (CFW.UI.InGameUI.InGameUI.Manager != null) + ", IsLoadComplete=" + CFW.UI.InGameUI.InGameUI.IsLoadComplete);
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ResendInGameUIOnLoad 异常: " + e); }
        }

        public static void ForceEndLoading()
        {
            try { CFW.Framework.GameEventHandler.DummySendEvent(CFW.Framework.GameEvent.Loading_End, null); Debug.Log("[CFZ-Offline] >>> GameEvent.Loading_End (强制关闭加载界面)"); }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] ForceEndLoading 异常: " + e); }
        }

        public static void LogDiag()
        {
            Debug.Log("[CFZ-Offline] ===== 诊断开始 =====");
            try
            {
                var rl = Common.System.ResourceLoader.Instance;
                Debug.Log("[CFZ-Offline] ResourceLoader.IsUseAssetBundle=" + (rl != null ? rl.IsUseAssetBundle.ToString() : "null"));
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] RL: " + e.Message); }
            try
            {
                Debug.Log("[CFZ-Offline][闸门] 白名单=" + (UseAssetWhitelist ? "开(只放行 角色/武器/装备/特效/音频)" : "★关(只要求包在本地清单里)★") +
                          " | 累计强制走bundle=" + _gateForce + " 次, IsBundle强制true=" + _gateBundle + " 次");
            }
            catch { }
            try
            {
                Debug.Log("[CFZ-Offline] LoadingUI.IsLoadingUIReady=" + CFW.UI.Loading.LoadingUI.IsLoadingUIReady +
                          ", isGameSceneLoadingComplete=" + CFW.UI.Loading.LoadingUI.isGameSceneLoadingComplete);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] LoadingUI: " + e.Message); }
            try
            {
                var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var lui = UnityEngine.Object.FindObjectOfType<CFW.UI.Loading.LoadingUI>();
                Debug.Log("[CFZ-Offline] LoadingUI 实例 = " + (lui != null ? "FOUND" : "NULL") + ", IsNowLoading=" + CFW.UI.Loading.LoadingUI.IsNowLoading);
                if (lui != null)
                {
                    var lt = lui.GetType();
                    string[] fns = { "UISet", "UISet_Solo", "UISet_Team", "UISet_Tutorial" };
                    for (int i = 0; i < fns.Length; i++)
                    {
                        var f = lt.GetField(fns[i], F2);
                        object v = f != null ? f.GetValue(lui) : null;
                        Debug.Log("    LoadingUI." + fns[i] + " = " + (f == null ? "字段未找到" : (v == null ? "NULL" : v.GetType().Name)));
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] LoadingUI 字段探测: " + e.Message); }
            try
            {
                Debug.Log("[CFZ-Offline] InGameUI.IsLoadComplete=" + CFW.UI.InGameUI.InGameUI.IsLoadComplete +
                          ", Manager!=null=" + (CFW.UI.InGameUI.InGameUI.Manager != null));
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] InGameUI: " + e.Message); }
            try
            {
                var ids = CFW.DataTable.DataTableManager.MapTable.GetMapIDs();
                Debug.Log("[CFZ-Offline] MapTable 共 " + ids.Count + " 张");
                for (int i = 0; i < ids.Count; i++)
                    Debug.Log("  MapID=" + ids[i] + " Scene='" + CFW.DataTable.DataTableManager.MapTable.GetSceneName(ids[i]) + "'");
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] MapTable: " + e.Message); }
            try
            {
                var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var rl2 = Common.System.ResourceLoader.Instance;
                var blF = rl2.GetType().GetField("bundleLoader", F2);
                var bl = blF != null ? blF.GetValue(rl2) : null;
                if (bl != null)
                {
                    Debug.Log("[CFZ-Offline] bundleLoader=" + bl.GetType().FullName);
                    var blistF = bl.GetType().GetField("bundleList", F2);
                    var blist = blistF != null ? blistF.GetValue(bl) : null;
                    if (blist != null)
                    {
                        Debug.Log("[CFZ-Offline] bundleList=" + blist.GetType().FullName);
                        var readyP = blist.GetType().GetProperty("IsBundleListReady", F2);
                        if (readyP != null) Debug.Log("[CFZ-Offline] IsBundleListReady=" + readyP.GetValue(blist, null));
                        string[] probe = { "dust2", "tutorial_1", "u_station_td", "inferno_newtm", "mirage_tm", "arena" };
                        for (int i = 0; i < probe.Length; i++)
                        {
                            object r = "?";
                            try
                            {
                                var m2 = blist.GetType().GetMethod("IsBundle", F2, null, new Type[] { typeof(string), typeof(string) }, null);
                                if (m2 != null) r = m2.Invoke(blist, new object[] { probe[i], probe[i] });
                                else
                                {
                                    var m1 = blist.GetType().GetMethod("IsBundle", F2, null, new Type[] { typeof(string) }, null);
                                    if (m1 != null) r = m1.Invoke(blist, new object[] { probe[i] });
                                }
                            }
                            catch (Exception e3) { r = "err:" + e3.Message; }
                            Debug.Log("[CFZ-Offline]   IsBundle('" + probe[i] + "')=" + r);
                        }
                        try { Debug.Log("[CFZ-Offline] MapTable.GetSceneName(1)='" + CFW.DataTable.DataTableManager.MapTable.GetSceneName(1) + "'"); } catch { }
                        try { Debug.Log("[CFZ-Offline] MapTable.GetSceneName(10001)='" + CFW.DataTable.DataTableManager.MapTable.GetSceneName(10001) + "'"); } catch { }
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] bundleProbe: " + e.Message); }
            try
            {
                var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var rl3 = Common.System.ResourceLoader.Instance;
                var blp = rl3.GetType().GetProperty("bundleLoader", F2);
                var bl3 = blp != null ? blp.GetValue(rl3, null) : rl3.GetType().GetField("bundleLoader", F2).GetValue(rl3);
                if (bl3 != null)
                {
                    var blProp = bl3.GetType().GetProperty("bundleList", F2 | BindingFlags.FlattenHierarchy);
                    var blist3 = blProp != null ? blProp.GetValue(bl3, null) : null;
                    if (blist3 != null)
                    {
                        var readyProp = blist3.GetType().GetProperty("IsBundleListReady", F2);
                        Debug.Log("[CFZ-Offline] bundleList=" + blist3.GetType().FullName +
                                  ", IsBundleListReady=" + (readyProp != null ? readyProp.GetValue(blist3, null).ToString() : "?"));
                        var m1 = blist3.GetType().GetMethod("IsBundle", F2, null, new Type[] { typeof(string) }, null);
                        string[] probe = { "Transportship_ren", "Dust2", "Tutorial_1", "dust2", "tutorial_1" };
                        for (int i = 0; i < probe.Length; i++)
                            Debug.Log("[CFZ-Offline]   IsBundle('" + probe[i] + "')=" + (m1 != null ? m1.Invoke(blist3, new object[] { probe[i] }).ToString() : "?"));
                        var gbi = blist3.GetType().GetMethod("GetBundleInfo", F2);
                        if (gbi != null)
                        {
                            object info = gbi.Invoke(blist3, new object[] { "Transportship_ren" });
                            if (info != null)
                            {
                                var it = info.GetType();
                                string n = it.GetField("Name").GetValue(info) as string;
                                object bt = it.GetField("BundleType").GetValue(info);
                                Debug.Log("[CFZ-Offline]   GetBundleInfo('Transportship_ren').Name='" + n + "' BundleType=" + bt);
                            }
                        }
                    }
                    else Debug.LogWarning("[CFZ-Offline] bundleList 属性为 null");
                    Debug.Log("[CFZ-Offline] Bundle 目录 = " + BundleDir());
                    string sd = BundleDir() + "/Scene";
                    Debug.Log("[CFZ-Offline] Bundle/Scene 存在=" + System.IO.Directory.Exists(sd) +
                              ", transportship_ren.unity3d 存在=" + System.IO.File.Exists(sd + "/transportship_ren.unity3d"));
                    if (System.IO.Directory.Exists(sd))
                    {
                        var fs = System.IO.Directory.GetFiles(sd, "*.unity3d");
                        Debug.Log("[CFZ-Offline] Bundle/Scene 共 " + fs.Length + " 个:");
                        for (int i = 0; i < fs.Length && i < 40; i++) Debug.Log("      " + System.IO.Path.GetFileName(fs[i]));
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] bundleListState: " + e.Message); }
            try
            {
                var uiAll = Resources.LoadAll("Prefabs/UI/InGameUI");
                Debug.Log("[CFZ-Offline] Resources 'Prefabs/UI/InGameUI' 共 " + uiAll.Length + " 个");
                for (int i = 0; i < uiAll.Length && i < 80; i++)
                    Debug.Log("    " + uiAll[i].name + " [" + uiAll[i].GetType().Name + "]");
                var uiAll2 = Resources.LoadAll("Prefabs/UI");
                Debug.Log("[CFZ-Offline] Resources 'Prefabs/UI' 共 " + uiAll2.Length + " 个");
                string[] modes = { "TD", "TM", "Tutorial", "Observe", "Nano", "DM", "DeathRun", "Jump", "League", "AI_Tutorial", "SPECIAL", "BR", "Solo" };
                for (int i = 0; i < modes.Length; i++)
                {
                    string p = "Prefabs/UI/InGameUI/InGameUI_Layout_" + modes[i];
                    var o = Resources.Load(p);
                    Debug.Log("[CFZ-Offline]   Resources.Load('" + p + "') = " + (o != null ? o.name + " [" + o.GetType().Name + "]" : "NULL"));
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] uiLayoutProbe: " + e.Message); }
            try
            {
                var a = UnityEngine.Object.FindObjectOfType<CFW.UI.InGameUI.InGameUI>();
                Debug.Log("[CFZ-Offline] FindObjectOfType<CFW.UI.InGameUI.InGameUI> = " +
                          (a != null ? ("FOUND activeInHierarchy=" + a.gameObject.activeInHierarchy + " enabled=" + a.enabled + " go=" + a.gameObject.name) : "NULL"));
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] CFW.InGameUI 探测: " + e.Message); }
            try
            {
                var b = UnityEngine.Object.FindObjectOfType<CFBR.UI.InGameUI.InGameUI>();
                Debug.Log("[CFZ-Offline] FindObjectOfType<CFBR.UI.InGameUI.InGameUI> = " + (b != null ? "FOUND" : "NULL"));
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] CFBR.InGameUI 探测: " + e.Message); }
            try
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var roots = sc.GetRootGameObjects();
                Debug.Log("[CFZ-Offline] ActiveScene='" + sc.name + "' 根对象 " + roots.Length + " 个");
                for (int i = 0; i < roots.Length && i < 40; i++)
                    Debug.Log("    [根] " + roots[i].name + " activeSelf=" + roots[i].activeSelf);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] 场景根对象: " + e.Message); }
            try
            {
                int cnt = UnityEngine.SceneManagement.SceneManager.sceneCount;
                Debug.Log("[CFZ-Offline] ===== 已加载场景数 = " + cnt + " =====");
                var active2 = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                for (int i = 0; i < cnt; i++)
                {
                    var s2 = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    Debug.Log("    [" + i + "] '" + s2.name + "' isLoaded=" + s2.isLoaded +
                              " 根对象=" + s2.rootCount + " path='" + s2.path + "'" +
                              (s2 == active2 ? "  <=Active" : ""));
                }
                var md = UnityEngine.Object.FindObjectOfType<CFW.Level.MapData>();
                Debug.Log("[CFZ-Offline] FindObjectOfType<CFW.Level.MapData>() = " +
                          (md != null
                            ? ("FOUND go=" + md.gameObject.name + " scene='" + md.gameObject.scene.name + "'" +
                               " activeSelf=" + md.gameObject.activeSelf +
                               " LevelData=" + (md.LevelData != null) + " ZoneData=" + (md.ZoneData != null) +
                               " PhysicsData=" + (md.PhysicsData != null) + " ResurrectionData=" + (md.ResurrectionData != null))
                            : "NULL"));
                var ig = UnityEngine.Object.FindObjectOfType<CFW.InGame.InGame>();
                Debug.Log("[CFZ-Offline] FindObjectOfType<CFW.InGame.InGame>() = " + (ig != null ? ("FOUND go=" + ig.gameObject.name + " ModeType=" + CFW.InGame.InGame.ModeType) : "NULL"));
                Debug.Log("[CFZ-Offline] CFW.InGame.InGame.PVCamera = " + (CFW.InGame.InGame.PVCamera != null ? "FOUND" : "NULL"));
                var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                Debug.Log("[CFZ-Offline] 场景相机数 = " + cams.Length);
                for (int i = 0; i < cams.Length && i < 10; i++)
                    Debug.Log("    相机[" + i + "] " + cams[i].name + " tag=" + cams[i].tag + " enabled=" + cams[i].enabled +
                              " depth=" + cams[i].depth + " scene=" + cams[i].gameObject.scene.name +
                              " pos=" + cams[i].transform.position);
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] 场景清单: " + e.Message); }
            try
            {
                string[] ln = { "AI_Tutorial", "Tutorial", "TD" };
                for (int i = 0; i < ln.Length; i++)
                {
                    var ta = Resources.Load<TextAsset>("Prefabs/UI/InGameUI/InGameUI_Layout_" + ln[i]);
                    if (ta == null) { Debug.Log("[CFZ-Offline] layout " + ln[i] + " = NULL"); continue; }
                    Debug.Log("[CFZ-Offline] ==== layout " + ln[i] + " XML (前700字) ====");
                    Debug.Log(ta.text.Substring(0, Math.Min(700, ta.text.Length)));
                    var ms = System.Text.RegularExpressions.Regex.Matches(ta.text, "Prefab\\s*=\\s*\"([^\"]+)\"");
                    for (int k = 0; k < ms.Count; k++)
                    {
                        string pn = ms[k].Groups[1].Value;
                        var po = Resources.Load(pn);
                        Debug.Log("[CFZ-Offline]    XML Prefab '" + pn + "' -> " + (po != null ? ("OK " + po.GetType().Name) : "NULL"));
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] layoutXML 探测: " + e.Message); }
            try
            {
                var gc = UnityEngine.Object.FindObjectOfType<CFW.Framework.GameClient>();
                if (gc != null)
                {
                    var F2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    var st = gc.GetType().GetField("currState", F2);
                    Debug.Log("[CFZ-Offline] GameClient.currState=" + (st != null ? st.GetValue(gc).ToString() : "?"));
                }
            }
            catch (Exception e) { Debug.LogWarning("[CFZ-Offline] GameClient: " + e.Message); }
            LogScenes();
            Debug.Log("[CFZ-Offline] ===== 诊断结束 =====");
        }

        public static void LogScenes()
        {
            try
            {
                int n = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
                Debug.Log("[CFZ-Offline] 构建场景总数 = " + n);
                for (int i = 0; i < n; i++)
                {
                    string p = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                    if (string.IsNullOrEmpty(p)) continue;
                    int s = p.LastIndexOf('/'); int d = p.LastIndexOf('.');
                    string nm = (s >= 0 && d > s) ? p.Substring(s + 1, d - s - 1) : p;
                    Debug.Log("  [" + i + "] " + nm);
                }
            }
            catch (Exception e) { Debug.LogError("[CFZ-Offline] LogScenes 异常: " + e); }
        }

        IEnumerator WaitFor(Type t, float timeout)
        {
            waited = null;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeout)
            {
                waited = UnityEngine.Object.FindObjectOfType(t);
                if (waited != null) yield break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        static void Inj(object target, object dh, string fieldName, Type dataType, object dt)
        {
            try
            {
                if (GetField(target, fieldName) != null) return;
                var m = dh.GetType().GetMethod("GetData").MakeGenericMethod(dataType);
                var v = m.Invoke(dh, new object[] { dt });
                if (v != null) { SetField(target, fieldName, v); Debug.Log("[CFZ-Offline] 已注入 " + fieldName); }
            }
            catch (Exception e) { Debug.Log("[CFZ-Offline] 注入 " + fieldName + " 失败: " + e.Message); }
        }

        static object GetAny(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, F);
                if (p != null && p.CanRead) { try { return p.GetValue(o, null); } catch { } }
                var f = t.GetField(name, F);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        static object GetField(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, F);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        static void SetField(object o, string name, object v)
        {
            if (o == null) return;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, F);
                if (f != null) { f.SetValue(o, v); return; }
            }
            Debug.LogError("[CFZ-Offline] 字段不存在: " + name);
        }

        static object Call(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(name, F);
                if (m != null) return m.Invoke(o, null);
            }
            throw new MissingMethodException(o.GetType().Name, name);
        }
    }
}
