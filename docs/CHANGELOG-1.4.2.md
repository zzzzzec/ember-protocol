# 1.4.2 · 安全箱与撤离设置

按用户要求，通过现有 SVM 2.1.2 的 sptz 预设调整。第三方 DLL 未修改。

- Items.EnableItems 和 RemoveSecureContainerFilters 开启。安全箱允许通用物品类别，排除列表清空；保留原有格子、容积和尺寸限制。原生后端核对了 15 种安全箱，包含 WTT 扩充后的过滤器。
- Raids.EnableRaids、EnableCarCoop 开启。ExtendedExtracts 忽略出生侧，ChanceExtracts 让概率撤离点必出；FreeCoop、NoBackpack、GearExtract、ArmorExtract 取消合作人数、背包、冰镐/绳索及护甲条件。各地图载具费用设为零，保留原有撤离倒计时。
- 八个开放地图变体的常规撤离点均以 100% 返回。中心区的五个点全部为单人、免费、无附加条件；海关 ZB-013、储备站地堡、实验室按钮、安全屋和列车等场景机制仍需原生触发。SVM 不会自动通电、开锁或召来列车。
- 启用 SVM Items 会使用其原生逻辑：最低搜刮/检查经验为 1、检查时间默认 1 秒，部分故障/热量数值取整。价格、重量、弹药装卸等倍率均保持 1，没有启用货币/弹药堆叠、自动检视等其他开关。

玩家运行中仅保存预设，不替换客户端或服务端 DLL，不停止游戏。预设原件备份在玩家安装的 .sptz/settings-backups；完整关闭服务器和游戏后重新启动即可读取设置。配置已纳入源码与发布包，后续安装会沿用。
