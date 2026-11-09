# 本机开发、安装与测试

把仓库克隆到本机后，在仓库根目录运行下方命令。

| 实例 | 目录 | 端口 | 用途 |
|---|---|---|---|
| Player | `D:\Games\SPT-Player` | 6969 | 正常游玩，保留现有角色 |
| Development | 项目内 `.local/instances/development` | 6974 | 独立后端、完整 QA 客户端和专用测试角色 |

开发实例现已具备匹配版本的完整客户端，用独立测试账号验证界面与战局。开发启动器须从自身 SPT 目录启动，私有 launcher 配置设置 IsDevMode=true 才会保留 6974；正式游玩仍使用 6969。安装、API、实际界面和战局验证分别记录，不能互相替代。

## 日常入口

在项目根目录双击：

- **安装到测试目录.cmd**：从当前源码构建，安装到 Development，再校验。
- **安装到游玩目录.cmd**：从当前源码构建，安装到 Player，再校验。先退出该目录的游戏、启动器和后端。
- **启动测试后端.cmd**：启动已安装的独立 6974 后端。
- 游玩时手动打开目标目录的 **SPT.Server.lnk**，再打开 **SPT.Launcher.lnk**；关闭时直接关对应窗口。

安装入口每次都先构建；构建失败不会继续安装。测试启动入口使用已安装版本。Player 不提供脚本自动启动；手动分别运行 SPT.Server 和 SPT.Launcher。

## 修改与验证

源码在 `src/`，玩法参数在 `config/gameplay.json`，任务文案与奖励在 `content/server/data/`，第三方设置在 `config/overrides/`。修改这些文件，避免直接改运行目录里的 DLL 或数据。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Check
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Install -Instance Development
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Start -Instance Development
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action BackendCheck -Instance Development
```

后端检查只使用开发目录里的专用测试角色，拒绝 Player。完成适用检查后，再安装到 Player 进行实际游玩验证。工具不会为安装而自动结束正在玩的游戏或后端。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Install -Instance Player
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Verify -Instance Player
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Workspace.ps1 -Action Rollback -Instance Player
```

回退恢复最新一次安装前的文件。实际设置被手动改动时默认报出冲突；BepInEx 仅改写换行、注释或排版时不会误报；SVM 预设仅改写 BOM、换行或文件末尾换行时也不会误报，设置值的变化仍受保护。账号和窗口偏好按本机当前值保留。

如果通过 F12 或 SVM 调整了设置，先用 `Inspect-Mods.ps1` 与 `pack.py capture` 显式采集，再审阅 Git 差异。不要为消除冲突盲目覆盖本地改动。旧游戏目录里的 `Tools/ZombieSurvival` 临时源码和发布脚本仅作为历史记录，不再作为开发入口。

## 本机路径配置与迁移

实际路径、工具链和端口存放在 `.local/workspace.json`，不提交 Git。换电脑、改盘符或调整实例目录后，重新配置一次：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Configure-Workspace.ps1 -PlayerRoot "D:\Games\SPT-Z" -DevelopmentRoot "D:\Games\SPT-Test" -DotNet "C:\Program Files\dotnet\dotnet.exe"
```

两个目标目录先具备匹配版本的 SPT 基底；开发与游玩目录不能相同或互相包含，也不能共用端口。默认从玩家目录读取编译引用，从 `artifacts/ember-dependencies.zip` 读取锁定依赖；没有归档时改从引用目录读取依赖。

首次接管现有安装时，真实存档与启动器配置另备份在玩家目录 `.sptz/pre-adoption/`；每次安装的文件备份在 `.sptz/backups/`。这些内容包含本机私有数据，不放进 Git。

## 剧情与修理边界检查

`BackendCheck` 依次检查主线、营地成长、返场补货、黑商路线及互斥接取、真实修理。新角色与合成战斗夹具仅用于 Development，不写真实角色。另行重启开发后端后，可运行 `native_story_check.py --game-root <Development目录> --port 6974 --report artifacts/native-story-verification.json --recheck-restart` 核对路线及领奖持久性。

开发启动失败时查看模组目录的 `startup-errors.jsonl`，不必依赖截图。诊断包同时包含启动、剧情规则、技能和仓库奖励快照及经济报告，并沿用现有隐私过滤。
