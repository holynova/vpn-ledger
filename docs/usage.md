# 使用指南

下载并双击 `VpnLedger.exe`，无需管理员权限或额外运行库。每次打开默认选择 VPN，外观默认跟随系统。

| 入口 | 可以查看什么 |
|---|---|
| VPN | 本机 Clash Verge / Mihomo 代理连接的上下行、用量排行、实时速率和最近 1 / 5 / 15 分钟趋势。 |
| 全局 | 所有已连接物理网卡的收发总量与速度，包括直连、VPN 出口和局域网。应用排行来自 Windows 网络用量统计，按系统提供记录的时间更新。 |
| 用量分布 / 全部应用 | 默认显示矩形图。应用较多时保留主要应用与未识别项，余量合并为一个色块；点「全部应用」可逐项查看，并在 VPN 模式按速率排序。 |
| 未识别应用 → 详情 | 在首页或历史排行中打开。VPN 记录包含可获取的目标域名、IP、端口、源端点、协议、入站类型、规则类型、观测时间和上下行。全局记录展示系统返回的标识和网卡。 |
| 历史 | 独立页面，按日期列出 VPN 和全局账本。点入可看上下行、应用排行、已留存的未知连接线索，以及全局账本的各网卡用量。返回键或 Esc 退回上一级。 |
| 设置 → 外观 | 跟随系统、浅色或深色；偏好在重新打开后继续生效。 |
| 暂停 | 停止当前模式采集；点「继续」重新建立基线。关闭窗口后留在托盘，双击托盘图标可返回。通过设置或托盘菜单可退出并保存。 |

## 两种统计口径

**VPN** 每秒只累计已确认代理出口的活动连接。直连、拒绝和无法判断线路的连接直接跳过。自动连接本机 Clash Verge Rev 生产命名管道，也识别名为 `verge-mihomo`、`mihomo` 的管道。进程优先取自内核，其次匹配 Windows IPv4/IPv6 连接表。Fake-IP 缺少目标地址时用本地端点和目标端口匹配，UDP 用本地端点匹配，两者均要求候选进程唯一。短连接和连接关闭前末段可能漏记。旧版只记录汇总，升级前的未知用量无法还原连接细节。

**全局** 独立于 Clash，按物理网卡读字节计数；每约 8 秒查询 Windows 为当前监控时间段提供的应用网络用量。Windows 的更新周期、进程归属及协议层与网卡不同，应用合计与网卡总量可能有差异。代理流量在这个排行中可能归入 Mihomo 进程，同名程序合并。系统尚未提供归属记录时，仍会更新网卡总量。

两种模式独立记账，一次只采集当前选中的模式。切换模式、恢复采集和长时间断采后重新建立基线。使用十进制 KB / MB / GB。

## 本机数据

数据位于 `%LOCALAPPDATA%\VpnLedger`。VPN 和全局历史分别保存为 `days.json` 和 `global-days.json`，每种模式最多保留 30 个有记录的日期。每个日期最多保存最近 80 条未识别连接或系统归属线索，超出上限仍保留字节合计。速度图在内存中保留 15 分钟。未知连接的域名与地址保存在本机，程序不记录请求正文。外观偏好保存在 `appearance.json`。

## 构建与验证

使用 Windows 和 .NET 8 SDK，在项目目录执行：

```powershell
dotnet run --project checks -c Release
dotnet run --project checks -c Release -- --live
dotnet publish src/VpnLedger.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o release
```

第一条验证 VPN 过滤、未知连接线索、两种模式的计数、旧版历史兼容和 TCP/UDP 进程匹配。第二条同时检查运行中的 Mihomo 及 Windows 网卡和按应用统计。
