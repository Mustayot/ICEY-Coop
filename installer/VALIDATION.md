# 2026-10-29 Boss / P2 小修（0b3bd106acea）

协议保持 4，双方更新同一个完整包。本次按用户“尽快收尾”要求仅完成以下范围，未继续扩大审计。

- Boss：UIBossHpBarController.boss 原生字段实际为 private static；修正反射标志和 GetValue/SetValue 的目标。可靠 HitResolved 到达时立即将 HP/护盾镜像到 EnemyAttribute，避免只更新显示缓存、等待周期快照。
- P2：移除按距 P1 远近、低于 P1、近期输入但位置不动而传送到 P1 的自定义恢复。正常同关卡只在持续深埋静态碰撞体或确已低于地图底边时，尝试回到 P2 自己的落地安全点；使用前重验场景、当前 PlayerRange 和碰撞净空。进新场景仍允许在合法净空位置靠近 P1。没有安全点则不盲传。
- 保留原生战斗门/Boss 场地 PlayerRange 限制和原生掉落处理。不是无限制分离相机方案，原生限制仍可能移动角色。

验证：旧 Unity Mono / C#5 全部生产程序集构建退出码 0；Core 536/0（含 8 项新增 P2 恢复策略用例）；安装到两份隔离副本均退出码 0；单 TCP 转发原生双端 r2 主机 44/0、客机 65/0、runner 0，自有进程/探针已清理。

第一次 runner 因探针写报告瞬间文件独占而退出 1，原始报告保留。Get-Result 仅对 Windows 32/33 共享/锁定冲突返回等待，其他 IO 错误仍抛出；原有 RESULT、截图、超时断言没有弱化。第二轮原生流程通过。原生流程覆盖建房/加入/退出重开与已有健康/快照回归，不冒充真实 Boss 战或全部低平台/击飞场景已验。

已知未解决：不同难度下 Boss 最大 HP/阶段数据不同，现有协议只同步当前 HP，仍可能造成比例或截断差异；同名多 Boss 的绑定歧义仍需后续权威实体 ID 设计。P2 原生场地限制、所有场景物理恢复、真实跨网和完整主线未全验。每秒刷新 P2 强化数据、重开后的 Encounter 生命周期仅为待查候选，本次没有修改。

完整 v5 Boss 草稿已隔离在 artifacts/deferred-boss-v5，未接入编译或发布，不可直接恢复冒充完成。手机版移植问题按用户最新要求搁置，未开始移植。

原始证据：artifacts/boss-hud-native-evidence.txt、boss-p2-lite-build.log、boss-p2-lite-core.json、boss-p2-lite-native.log（失败）、boss-p2-lite-native-r2.log（通过），以及 tests/InstallerRuntime/evidence/boss-p2-lite-final 与 boss-p2-lite-r2。原版游戏只读、不启停；仅操作两份自有测试副本。

复核命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -ProjectRoot <本仓库> -GameRoot D:\ICEYCoop\test-game -Configuration BossP2Lite
dotnet run --no-restore --project tests/Core.Specs/Core.Specs.csproj -- --report artifacts/boss-p2-lite-core.json
powershell -NoProfile -ExecutionPolicy Bypass -File tests/InstallerRuntime/run-forwarded-lobby-test.ps1 -Name <新的证据名> -PortableBoot -TimeoutSeconds 120
```

不要为复核随手重编后混装旧包；构建标识来自实际 DLL。源码权威分支 fix/boss-hud-p2-recovery-20261029；本地提交，不推送。之前的 9be42f6b7ecb 发布包保留。

---

以下为上一版 9be42f6b7ecb 的历史验收，未作为本版新增玩法验收。

# 2026-10-29 游戏体检版验收

构建 `9be42f6b7ecb`，协议 4。此文件列出实际通过与未验范围，不能将体检完成等同于整个游戏没有 bug。

| 验证 | 实际结果 |
| --- | --- |
| Core/Protocol/Transport 全套回归 | 528 通过，0 失败，退出码 0 |
| 旧 Unity Mono / C#5 全部生产程序集构建 | build5 退出码 0；Bootstrap 元数据自检通过 |
| 本版安装与校验到两份隔离完整游戏 | 双方 Install/Verify 退出码 0，23 项载荷校验一致 |
| 主菜单单 TCP 转发双端原生流程 | 第三轮 host 44/0，client 65/0，runner 0；包含加入、退出、重开、保存保护与角色清理 |
| 游戏内运行采样和报告 | 双方角色、真实绑定/保护、输入序号与有效回执、客机快照时效及 JSON 导出通过 |
| 实际客户端 handler 回归 | 受控快照的新包后旧包、重复分片和旧关卡基线包不回写呈现；中途加入的实体版本、快照间连续 HitResolved 事件游标正常推进 |
| F8 面板 | 检查了真实游戏渲染截图，中文文本、滚动区与导出控件可读；未将程序打开面板当作真人按键验收 |
| 网页安装器回环 HTTP | Windows PowerShell 5 实际字节帧用例通过；中文 body、鉴权、方法限制、超大/半截请求与读取超时；退出码 0 |
| 网页关闭与动作完成 | 受控 fake engine 通过真实 runspace 证明关闭时不中断动作、完成后自然退出；这项只验证生命周期，不冒充真实安装事务正确性 |
| 网页安装器 SelfTest | SELFTEST_OK，退出码 0 |
| 原工程镜像检查 | doctor 13 pass / 2 warn / 0 fail；两条 warn 为旧有场景白名单环境未设置 |
| 原版与测试清场 | 原版 6 项已知关键文件哈希不变；自有测试游戏和探针已清理 |

保留的失败历史：第一轮 native runner=1，client49/0，host44/2。主机断言读取了尚未刷新的一秒缓存样本（P2Loading、接受序号0）；第二轮等待真实 Active/接受输入样本后再执行相同断言，host44/0、client49/0。没有删除或放宽断言。该轮还发现摘要把 Pass 排在 Waiting 之上；用正确的加载中样本对旧代码复现 RED=1，新实现 GREEN=0。最初新增摘要测试夹具把非零接受序号与 age=-1 混用，现已修正夹具并保留无效数据检查，原失败报告也保留。

快照顺序与可靠事件策略有真实消息 codec 的纯逻辑串联回归，以及运行中的实际客户端 handler 受控回归。受控 HP 数值只用于判断是否发生旧状态回写，**不是原生攻击造成伤害的证明**。真实跨电脑、第三方 VPN/穿透、完整主线/全部 Boss、跳跃隔离及 P2 大招等仍未全验，不宣称全部玩法通过。

当 UDP 快照领先可靠事件时，客机会等事件游标追平，依靠后续周期样本恢复呈现；积压期间可能有短暂视觉延迟，体检会显示事件差值并检测快照过期。现有快照 wire 没有独立 session/epoch 标记，当前屏障不承诺绝对识别跨主机进程重启的所有旧包。

双方须安装同一完整包。合作档保留，但不同构建旧档仍受原有兼容门禁约束；不强行绕过，不替换 Steam 单人进度。包不含游戏本体、个人存档、测试探针、测试隔离 patcher 或开发依赖。使用说明见 GameHealth.md。