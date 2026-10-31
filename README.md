# ICEY Coop

这是《ICEY》双人联机模组的完整开发仓库，包含游戏桥接、联机协议、传输、回归测试和安装器源码。

**玩家下载：[GitHub Releases](https://github.com/Mustayot/ICEY-Coop/releases)**。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/` | 全部模组源码：Bootstrap、Bridge、Core、Protocol、Transport、GameBridge、RelayHost |
| `tests/` | 纯逻辑、网页安装器及两副本原生运行回归 |
| `tools/` | 旧 Unity Mono 构建、载荷更新、打包、隔离验证工具 |
| `docs/` | 开发交接和证据边界 |
| `installer/` | 终端/网页安装器源码、界面、清单、冻结载荷、用户说明及第三方许可证 |
| `artifacts/` | 本地构建、诊断、发布输出；不提交 Git |

安装器是工程的一部分。仓库根目录用于开发，分发包在打包时会将 installer 内的安装入口放到 ZIP 顶层，玩家仍可直接运行 Setup.cmd。

## 构建与测试

Windows、Windows PowerShell 5.1、系统 .NET Framework C# 编译器，以及自己合法安装的受支持 ICEY 版本。纯逻辑测试另需 .NET 10 SDK。游戏 DLL 只从本机游戏目录只读引用；BepInEx/Harmony 编译引用来自本仓库 installer/payload，构建不会启动或修改游戏。

```powershell
# 在仓库根目录执行；替换为你自己的游戏目录
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -GameRoot "D:\你的游戏目录\ICEY" -Configuration Debug

dotnet run --project tests/Core.Specs/Core.Specs.csproj -- --report artifacts/core-report.json

# 将自己构建且验证过的产物写入安装器载荷，再打包
powershell -NoProfile -ExecutionPolicy Bypass -File tools/update-runtime-payload.ps1 -Configuration Debug -Version "你的版本号"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/pack-health-release.ps1
```

发布结果位于 artifacts/dist。打包会核对载荷和归档全部文件的 SHA256/长度，拒绝覆盖既有发布证据。新建 GameBridge .cs 必须加入 tools/build.ps1 的显式构建表；Core 源文件由通配清单编入。不要只编译 Core 测试就宣称游戏桥接可构建。

网页安装器回归：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/WebSetup.Tests.ps1
```

原生双端测试需要手动准备独立测试游戏副本及隔离探针环境，见 tests/InstallerRuntime 与 docs，不能对用户原版或正在游玩的进程执行。

## 当前验证范围

最新冻结构建 `df7238d23bbd`，协议 4。本版修复客机退出后的 P1/SHI 视觉恢复，专项11项断言与旧Mono生产构建通过。主机44项、客机65项原生流程属于上一版历史验证，本次未重跑游戏画面；完整逻辑回归结果见本次修复记录。

这些结果不代表所有 Boss、完整主线、跨电脑公网或所有物理场景全验。不同难度下 Boss 最大血量/阶段同步仍待处理，原生战斗场地限制保留。详情见 [验收记录](installer/VALIDATION.md) 与 [最新小修交接](docs/交接-20261029-Boss与P2小修.md)。

双方安装同一完整发布包；编译后的构建标识不同不能默认混联。个人游戏本体、Steam 单人存档、令牌、诊断日志和测试进程不随源码上传。

## 发布约定

- Git 保存源码、测试、工具及 installer 打包输入。
- GitHub Releases 附件保存可用安装包 ZIP 与 SHA256 校验文件。
- 不向 Git 提交游戏本体、个人存档或 artifacts 输出。
- 历史提交和旧发布保留；不通过强制推送重写历史。

第三方组件许可与对应源码见 installer/licenses、installer/third-party-source 及 installer/THIRD-PARTY.txt。
