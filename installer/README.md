> 最新构建 df7238d23bbd（2026.10.29-visibility.1）：修复退出客机后P1/SHI视觉未恢复。专项11项与旧Mono构建通过；本版没有重跑原生游戏画面，下面旧构建记录作为历史。

> 2026-10-29 小修构建 0b3bd106acea：修正 Boss 静态血条绑定及受击镜像，收紧 P2 自定义恢复，双方更新同包。不同难度最大 HP 等问题仍待修；本次验收边界见 VALIDATION.md。

# ICEY 双人联机模式 · 终端离线安装包

> 2026-10-29 游戏体检版：游戏内按 **F8** 查看角色绑定、输入/回执、快照与运行错误，并可导出报告。使用方法和已知边界见 [GameHealth.md](GameHealth.md)。双方必须安装相同构建。

双方使用同一个完整解压包，不能只发某个DLL。不需要另外安装BepInEx、Unity、.NET SDK或Python；安装器使用Windows自带Windows PowerShell 5.1，游戏沿用自己的Unity Mono。

## 安装

1. 先正常退出ICEY。确认你的游戏本体可以正常从Steam启动。
2. 将本ZIP完整解压到任意普通目录。不要在压缩包浏览器里直接双击运行。
3. 双击 `Setup.cmd`，会用默认浏览器打开图形界面：确认游戏目录（一般已自动填好），点“安装 / 更新”，完成后按提示操作。关掉页面即退出安装器；浏览器不可用时会自动退回窗口版/终端版。
4. 两人各自安装同一份包，然后按原方式从Steam启动游戏。在主菜单右下角点击“进入联机”。安装器不会替你启动或关闭游戏。

## 命令行安装/检查/卸载

（图形界面出问题时的备用路径，在解压目录打开终端）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1 -Action Install -GameRoot "你的ICEY目录"
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1 -Action Verify -GameRoot "你的ICEY目录"
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1 -Action Uninstall -GameRoot "你的ICEY目录"
```

## 联机

- **主机**：联机 → 创建房间，默认端口57000，可修改，然后进入游戏。
- **客机**：联机 → 加入房间，填写主机IP或域名:端口。
- **樱花内网穿透**：主机创建一个TCP隧道，目标为本机监听端口；好友填写隧道的公网域名:公网端口。
- **Radmin VPN/支持虚拟局域网的UU云联机**：双方先加入同一网络，再填主机虚拟网卡IP:游戏监听端口。

## 校验与版本

本包只支持清单列出的64位ICEY本体指纹。游戏版本不符、文件缺失、未知winhttp/不兼容BepInEx、文件被改或目标运行中会拒绝，避免半安装或混用依赖。

安装会保留原文件备份并验证全部运行文件。重新安装同版本可重复执行。文件校验成功表示安装完整，不能证明公网连通或所有剧情已验收。

新旧GameBridge构建标识不同不能混联。合作存档也有构建兼容门禁，升级前的文件会保留，但不强行跨构建恢复。

## 存档与卸载

不替换游戏本体EXE/DLL，不操作Steam userdata、不修改防火墙/VPN。备份与安装记录位于游戏目录 `BepInEx\icey-coop`。

合作存档 `coop-save.txt/.bak`、最近地址、日志等游戏过程中产生的数据会保留。卸载只处理本安装器拥有的文件，未改过的原文件还原安装前状态；其他模组/用户修改不强制删除。

如果新装的加载器已被其他插件使用，卸载会保留共享加载器；如果用户更改了BepInEx配置则保留该配置。已经被修改的自家插件会提示冲突，先备份并核对，不会强删。

## 排错

请先运行“校验”。再查看游戏目录 `BepInEx\LogOutput.log`、`BepInEx\icey-coop\logs` 和 `run`。

出现端口占用可换端口；连接超时需核对地址、端口映射/组网及玩家系统已有防火墙设置。安装器不会自动改系统网络设置。

若安装路径无写权限，使用你有写权限的游戏位置或自行决定是否以有权限的终端运行；安装器不会自动提权。
