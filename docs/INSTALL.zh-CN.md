# 安装、迁移与回退

目标电脑先准备 SPT 4.0.13、对应 EFT 0.16.9.40087 及其原生 BepInEx/SPT 客户端组件。安装器会校验关键程序集。包内不含游戏本体，也不会创建或覆盖真实角色。

先关闭**目标目录**的游戏、启动器和后端。其他目录的游戏实例无需关闭。

## 从现有安装目录获取依赖

解压 `ember-protocol-2.21.6.zip`，在解压目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GameRoot "D:\Games\SPT" -DependencyRoot "E:\Existing-SPT-Z"
```

若目标目录已具备所有匹配的依赖，可以省略 `-DependencyRoot`。原版配置、旧模组或其他版本不满足锁定清单时，会停止并报告具体文件。

先查看计划而不写文件：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GameRoot "D:\Games\SPT" -DependencyRoot "E:\Existing-SPT-Z" -Plan
```

## 换电脑

在原电脑，从**已经匹配当前锁定清单**的安装目录导出依赖：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Export-Dependencies.ps1 -SourceRoot "E:\Existing-SPT-Z" -Output "E:\Transfer\ember-dependencies.zip"
```

把发布包和依赖 ZIP 一起带到新电脑，解压发布包后运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GameRoot "D:\Games\SPT" -DependencyArchive "D:\Transfer\ember-dependencies.zip"
```

每个依赖文件都会校验 SHA-256。依赖档不含角色、启动器账号或日志。它与本包的 `mods.lock.json` 一一对应，不能拿另一个版本的依赖档混用。

如需搬迁存档，在确认模组组合一致后，另行备份并迁移 `SPT/user/profiles`；它不属于 Git 和此发布包。

## 独立开发实例

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GameRoot "D:\SPT-Test" -DependencyArchive "D:\Transfer\ember-dependencies.zip" -BackendPort 6974 -InstanceKind Development
```

实例应使用独立目录和角色。实例端口、日志标记和启动器路径在本机生成。默认玩家端口为 6969。

安装后分别打开 `SPT.Server.lnk` 与 `SPT.Launcher.lnk`，也可直接打开 `SPT/SPT.Server.exe` 与 `SPT/SPT.Launcher.exe`。关闭对应窗口即可停止。

## 验证和回退

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Verify.ps1 -GameRoot "D:\Games\SPT"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Rollback.ps1 -GameRoot "D:\Games\SPT"
```

备份和文件归属记录位于目标的 `.sptz/`。回退恢复最新一次安装前的文件和上一个安装记录；连续更新可以逐次回退。不要跨顺序执行旧备份，也不要在游戏运行时安装或回退。

更新会移除**上一版清单拥有且新版已删除**的文件，其他文件保留。清单内文件若被手动修改，默认会报告冲突。先把需要的设置采集到 Git 并重新构建；确实要用发布包覆盖时，再显式添加 `-OverwriteLocalChanges`，覆盖前仍会备份。

启动器账号、窗口偏好和 HTTP 配置的其他字段保留本机当前值；安装和回退只调整其中的游戏路径、服务器地址及端口。校验时也只检查这些受本包管理的运行参数。

安装器不会把当前文件校验通过等同于游戏内表现已经验证。实际界面、战斗和迁移存档仍需在目标环境检查。

开发仓库中的本机安装与测试入口见 `docs/WORKFLOW.zh-CN.md`。BepInEx 仅自动改写换行、注释或配置顺序时按实际键值校验；SVM 预设仅 BOM/换行差异也可识别；手动修改参数仍按本地配置冲突处理。
