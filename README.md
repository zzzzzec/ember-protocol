# 余烬协议 · Ember Protocol

适配 SPT 4.0.13 和 EFT 0.16.9.40087 的丧尸生存整合包。本次公开的是 **2.19.0** 源码，包含服务端和客户端插件、285 项任务、交易与制作数据、中文文案、原创商人插图和安装工具。

- 六张地图采用有限感染者总量和分批补刷。视野、声响和距离影响遭遇，普通撤离仍按场景规则运行。
- 商人、补给和藏身处随剧情逐步开放。枪弹、护甲和医疗偏稀缺，战斗和搜图能积累装备与技能。
- 安装工具区分游玩与开发实例，并在更新时校验依赖、备份包管理的文件。

## 本地构建与安装

准备匹配版本的现有游戏安装、Python 3.8+ 和 .NET SDK 9.0.318。游戏程序集用作本机编译引用，第三方依赖按 `mods.lock.json` 从自己的安装或依赖归档提供。

```powershell
python tools/pack.py validate
python -m unittest discover -s tests -v
pwsh -File tools/Build.ps1 -GameRoot 'D:\Games\SPT-Player' -DotNet 'C:\Program Files\dotnet\dotnet.exe'
```

生成的整合包位于 `dist/ember-protocol-2.19.0.zip`。安装、隔离开发、验证和回退命令见 [安装说明](docs/INSTALL.zh-CN.md) 与 [开发工作流](docs/WORKFLOW.zh-CN.md)。

## 项目目录

| 目录 | 内容 |
|---|---|
| `src/` | 服务端、客户端与配套插件源码 |
| `content/` | 任务、交易、配方、文案及插图 |
| `config/` | 玩法参数和依赖配置 |
| `tools/`、`tests/` | 构建、安装、校验与测试 |

公开源码与游戏本体、第三方二进制、依赖归档、个人存档和本机诊断数据分开管理。`content/server/data/profiles.json` 存放用于创建角色的模板。
