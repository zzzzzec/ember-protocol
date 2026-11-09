# 本机管理入口

`Tools/EmberProtocol/Admin.ps1` 随安装包部署；源码在 `tools/Admin.ps1`，执行器在 `src/server/LocalAdminCommands.cs`。这是一组本机管理命令，不需要再开网络端口。正常游玩不会自动发补给或跳过任务。

先用 Profiles、Items、Quests 找到精确目标。下面仅展示格式，运行 Grant/Quest 会在目标安装中排队；加 `-Plan` 只预览。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Games\SPT-Player\Tools\EmberProtocol\Admin.ps1 -GameRoot D:\Games\SPT-Player -Action Profiles
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Games\SPT-Player\Tools\EmberProtocol\Admin.ps1 -GameRoot D:\Games\SPT-Player -Action Items -Search 止血
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Games\SPT-Player\Tools\EmberProtocol\Admin.ps1 -GameRoot D:\Games\SPT-Player -Action Quests -Profile Example-PMC
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Games\SPT-Player\Tools\EmberProtocol\Admin.ps1 -GameRoot D:\Games\SPT-Player -Action Grant -Profile Example-PMC -Item 573719762459775a626ccbc1 -Count 48 -Note 补给 -Plan
```

发放使用 Grant / Item / Count；任务使用 Quest / Quest / State，其中 State 为 Ready 或 Success。Profile 匹配账号名或昵称，必须唯一。物品可按模板 ID 指定；Items 从本机原版手册与中文语言文件检索，后端按实际加载的物品表作最终校验。枪械使用完整原生预设，弹药按堆叠上限分组，发放物品默认非 FIR，通过军需官原生邮件领取，有效期一年。

Ready 将已经接受、尚未结束的本包任务置为可提交。特殊任务的上交/归还校验仍然存在。Success 直接标记该任务成功并更新后续解锁，**不补发经验、好感、现金、训练或物品奖励**；需要补偿时另发明确物品。未接受、已失败、已完成的任务不能用此入口重开，原有借枪记录与剧情选择不会删除。

指令在下次启动 Server、允许客户端连接之前执行，不会在玩家战局中并发改档。命令文件放在目标安装 `.sptz/admin/inbox`，编号、执行结果和审计保存在 receipts / audit.jsonl，执行前的完整角色备份位于 backups / 指令编号。每次指令编号在同一次角色保存中登记，重启和重复投递不会重复发放。无效指令转入 failed，服务器继续启动。排队后用 Status / CommandId 查看结果；尚未启动显示 PendingNextServerStartup。

这些目录是本机运行数据，不进入 Git 或依赖包；升级安装不会代替管理操作，也不会自动执行任何示例。恢复备份需要关闭该安装的客户端、启动器和 Server 后另行明确操作。

开发验收使用 `tools/native_admin_check.py`，仅允许 Development 与非 6969 端口。按 prepare → 重启开发后端 → verify → 再重启开发后端 → verify-replay，验证原生邮件、完整枪械、拆堆、任务状态、无隐式奖励、错误拒绝及重复指令持久性。不能把该测试指向 Player。

2.0.1 起，指令按 createdUtc 排队，编号只用于时间相同时的稳定排序；旧格式或无效文件以文件创建时间参与排序，再接受常规校验。每次启动最多处理 100 条。被原生服务标记为无效或无法加载的存档，在备份和修改前拒绝，避免原生保存跳过后误报成功。开发验收会故意倒排指令编号，并使用另一个独立的无效存档测试拒绝路径。
