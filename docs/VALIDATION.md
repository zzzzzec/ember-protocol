# 公开版本验证

2.21.6 内容校验涵盖 295 项任务、652 条共用商品、190 项兑换和 71 个配方。通过 34 项安装器与工作区测试、118,884 项玩法规则检查和客户端原生绑定检查。工厂昼夜生成和各档丰度的开发服抽样见 [2.21.6 更新记录](CHANGELOG-2.21.6.md)。

复核内容和安装规则可运行以下命令。

```powershell
python tools/pack.py validate
python -m unittest discover -s tests -v
```

游戏基底和工具链已经配置时，使用 `tools/Workspace.ps1 -Action Check` 追加玩法规则与绑定检查。安装校验、后端生成和实际进图表现分别验收。
