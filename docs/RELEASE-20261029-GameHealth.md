# 游戏体检版发布检查 · 2026-10-29

本轮目标：以GitHub main fc2c712为基线，改进游戏运行体检，修复审查中可复现的输入回执、旧快照呈现和连续事件问题。分支 improve/runtime-health-20261029；只做本地提交，尚未推送。

运行构建 9be42f6b7ecb，协议4。完整安装包 ICEYCoop-GameHealth-20261029-9be42f6b7ecb.zip，961758字节，43文件，其中23项运行载荷。

ZIP SHA256：7BE26ECE79176C23F775056881602B37084D776110D88B5B07F3F946AB4425D3。

最终ZIP全部43个entry的长度与SHA逐项核对。实际解压后用包内Setup分别对两份自有完整游戏执行Install（AlreadyInstalled）/Verify，并运行包内Web SelfTest，全部exit0。包内源码工具及游戏资产不混入运行载荷；不含测试probe、存档或隔离patcher。

生产源码145个文件的冻结哈希见runtime-health-source145.json。旧Mono生产build5 exit0，Core528/0；主菜单单TCP转发原生第三轮host44/0、client65/0、runner0；Web实际回环字节帧与关闭时runspace生命周期fixture exit0，SelfTest exit0。详细边界与保留失败见runtime-health-validation-20261029.md及根目录VALIDATION.md。

原D工程20项代码/测试改动已在逐项验证旧文件仍等于导入基线后备份并镜像，Debug/RuntimeHealth目录保存同一冻结构建，测试副本staged-manifest与实际安装一致；doctor13通过/2告警/0失败，原版6个关键文件哈希不变。D工程本轮备份 runtime-health-20261029-9be42f6b7ecb 保留旧版本。Git源代码的权威分支仍是本独立checkout。

没有宣称所有游戏bug修完，没有把受控HP呈现样本当真实攻击伤害；真实跨网、全主线、跳跃隔离和P2大招等旧未验项仍保留。旧合作档保留但不绕过跨构建存档兼容门禁。