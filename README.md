# PerfDeck · 副屏性能监控

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20WPF-512bd4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d6.svg)](#)

把一块闲置的副屏变成实时硬件监控面板：**CPU / 显卡 / 内存 / 磁盘 / 网络 / 风扇 / 电池 / 天气 / 热搜**
一共 16 种卡片，拖到哪放到哪，还能通过一个**只读的本地 HTTP 接口**让脚本取数。

- **不抢焦点**：可选点击穿透（`WS_EX_TRANSPARENT`），副屏看数据，主屏照常干活
- **单文件 exe**：自带 .NET 运行时，双击即用，不需要装 SDK
- **离线优先**：传感器全部本地读取，只有天气和热搜要联网，都可以关掉
- **对脚本友好**：`http://127.0.0.1:17821/api/v1/snapshot` 一个 GET 拿到全部硬件快照

---

## 卡片

| 分类 | 卡片 | 内容 |
| --- | --- | --- |
| 处理器 | CPU | 占用率、温度、最高频率、线程数、功耗 |
| | 显卡 / 核显 | 占用率、温度、频率、显存、功耗（独显 / 核显分开显示） |
| | 进程排行 | 按 CPU 或内存排序的前 N 名（3~10 条可调） |
| 存储 | 内存 | 已用 / 总量 / 可用、分页 / 非分页池 |
| | 磁盘 | 温度、占用率、活动率、健康度、读写速率 |
| 网络 | 网络状态 | 延迟、抖动、丢包、上下行速率、链路速率、IPv4/IPv6/网关/DNS/MTU |
| 散热 | 风扇 | 转速 RPM / 百分比，按所在硬件分组 |
| 其他 | 时钟 | 自定义时间格式与日期格式、独立字体 |
| | 天气 | 当前温度与图标，自动定位或手动指定城市 |
| | 电池 | 电量、充电状态、剩余时间 |
| | 今日热搜 | 综合 / AI 进展 / 微博 / 百度 / B站 / 抖音 / 头条，可点击跳转 |

卡片支持拖拽、缩放、吸附网格、统一尺寸自动排版，布局可锁定（`Ctrl+Alt+L`）。
每张卡片能单独设主色与标题；异常值按阈值变色（占用率、温度、延迟、丢包各有黄/红两级界线）。

## 外观

- **6 套配色**：深空蓝、石墨黑、极光绿、星云紫、暮橙、日光（浅色）
- **壁纸背景**：支持任意图片，模糊 + 暗化 + 卡片不透明度三项独立可调
- **窗口**：无边框圆角、可选置顶、整体不透明度、工具栏自动隐藏（鼠标靠近才出现）
- **趋势曲线**：卡片内的迷你 sparkline，纯 WPF 绘制，不引入图表库

## 性能挡位

监控本身不该成为负担，所以把「刷新频率 / 趋势曲线 / 进程条数 / 热搜条数」打包成三档：

| 挡位 | 刷新 | 趋势曲线 | 进程 | 热搜 | 内存预估 |
| --- | --- | --- | --- | --- | --- |
| 省内存 | 3 s | 关 | 3 条 | 3 条 | 150–175 MB |
| 均衡（默认） | 1 s | 开 | 5 条 | 5 条 | 200–225 MB |
| 流畅 | 0.5 s | 开 | 8 条 | 10 条 | 230–255 MB |

另有一项**智能降频**：检测到全屏游戏或重型任务时自动把刷新间隔拉长到 5 秒，
退出后再恢复——打游戏时不会跟你抢 CPU。

## 本地 HTTP 接口（v1）

默认监听 `127.0.0.1:17821`，**只绑回环、只读、无鉴权**，端口可在设置里改，填 0 关闭。

| 路径 | 说明 |
| --- | --- |
| `/api/v1/health` | 存活与运行状态 |
| `/api/v1/version` | 应用与接口版本 |
| `/api/v1/schema` | 字段自描述（给不写死字段的脚本用） |
| `/api/v1/snapshot` | 完整硬件快照 |
| `/api/v1/summary` | 精简摘要（只要几个关键数时用这个） |

```bash
# 看 CPU 占用与温度
curl http://127.0.0.1:17821/api/v1/snapshot
```

```jsonc
{
  "cpu":    { "name": "...", "loadPercent": 12.4, "tempC": 46, "maxClockMhz": 4300, "threads": 20, "powerW": 38.2 },
  "gpus":   [{ "name": "...", "integrated": false, "loadPercent": 3.1, "tempC": 41, "memUsedGb": 1.2, "memTotalGb": 8 }],
  "memory": { "usedGb": 12.6, "totalGb": 32, "usedPercent": 39.4 },
  "net":    { "latencyMs": 8, "lossPercent": 0, "downBps": 125000, "upBps": 4200, "ipv4": "..." },
  "disks":  [{ "name": "...", "tempC": 38, "usedPercent": 61.2, "readBps": 0, "writeBps": 1024 }],
  "fans":   [{ "name": "...", "rpm": 980 }],
  "battery":{ "present": false },
  "processes": [{ "name": "chrome", "cpuPercent": 4.2, "memoryMb": 812 }]
}
```

接口约定写死在这个 v1 里：**只增字段，不改名、不删字段**；破坏性变更会另开 `/api/v2`。
取不到的值一律给 `null`，而不是 `0`——调用方能区分「没有这项硬件」和「确实是 0」。

不想开界面也能取一次数（写进 `%AppData%\PerfDeck\api-dump.json` 后退出）：

```bash
PerfDeck.exe --api-dump
```

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--settings` | 启动后直接打开设置窗口 |
| `--api-port N` | 本次运行临时改接口端口（不写回配置） |
| `--api-dump` | 采一次快照写 JSON 后退出，不建窗口 |
| `--dump` | 导出传感器清单与一次采样到 `sensors.txt`，用于排查读不到温度的问题 |
| `--hotcheck` | 抓一轮热搜并写日志后退出，用于排查某个平台抓不到 |
| `--no-elevate` | 跳过自动提权（以普通权限运行） |

## 从源码运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/KukaNo996/PerfDeck.git
cd PerfDeck
dotnet run
```

打包成单文件 exe（`csproj` 里已经配好自包含 + 单文件 + 压缩）：

```bash
dotnet publish -c Release -r win-x64
# 产物：bin/Release/net8.0-windows/win-x64/publish/PerfDeck.exe
```

或者用仓库里的脚本，一步产出 `dist/`（exe + zip + 校验和）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\make_release.ps1
```

### 自动发版

推一个 `v*` 标签，GitHub Actions 会自动构建并创建 Release（`.github/workflows/release.yml`）：

```bash
git tag v1.0.0
git push origin v1.0.0
```

Release 里会附上 `PerfDeck.exe`（裸单文件）、`PerfDeck-v1.0.0-win64.zip`（exe + 说明 + 许可证）
和 `SHA256SUMS.txt`。在 Actions 页面点 **Run workflow** 则只构建、不建 Release，产物在
workflow 的 Artifacts 里留 14 天。

## 快捷键

| 组合 | 作用 |
| --- | --- |
| `Ctrl+Alt+M` | 最大化 / 还原 |
| `Ctrl+Alt+L` | 锁定 / 解锁布局 |
| `Ctrl+Alt+S` | 打开设置 |
| `Ctrl+Alt+X` | 退出 |

右键点面板任意处也有同样的菜单。

## 配置

配置在 `%AppData%\PerfDeck\config.json`，日志在同目录 `perfdeck.log`（超过 256 KB 自动截断）。
**便携模式**：把 `config.json` 放到 exe 同目录，程序会优先读它——适合放 U 盘带着走。
写入是「临时文件 + 替换」，不会因为断电写出半个损坏的配置。

## 已知限制

- **读温度/风扇/功耗需要管理员权限**（传感器要加载内核驱动）。程序默认非提权启动，
  检测到读不到传感器时才会以管理员身份重启自己，可用 `--no-elevate` 禁止。
- 开启 **内存完整性 / 核心隔离（HVCI）** 时，传感器驱动可能加载失败，温度与风扇会是 `null`。
  部分带反作弊的网游也会拦截这类驱动——这是驱动层的限制，不是面板的问题。
- 天气的自动定位走公开 IP 定位服务，城市可能判到运营商机房所在城市；要准确就在设置里手动填城市。
- 热搜来自各平台的公开端点，对方改接口就会抓不到（`--hotcheck` 可定位是哪个平台），
  这属于第三方接口，不做稳定性保证。
- 仅支持 **Windows 10/11 x64**：WPF + 大量 Win32 API（`PerformanceCounter`、`GetSystemMetrics`、
  `DwmExtendFrameIntoClientArea` 等），没有跨平台计划。

## 隐私

- 传感器数据全部本地读取，**不上传任何数据**；
- 联网只发生在两处：天气（`open-meteo` / `wttr.in` / IP 定位）与热搜（微博/百度/B站/抖音/头条/HN 的公开接口），
  两者都能在设置里关掉；
- 本地接口只绑 `127.0.0.1`，不对外网开放，也不触发防火墙弹窗；
- 不写注册表（除了开机自启用的那条 `Run` 键）、不装服务、不留后台进程（关窗即退）。

## 致谢

- 传感器与驱动：[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（`LibreHardwareMonitorLib`）
- 天气数据：[Open-Meteo](https://open-meteo.com/)、[wttr.in](https://wttr.in/)、[ip-api](https://ip-api.com/)
- 界面：WPF（`net8.0-windows`）+ 系统 `PerformanceCounter`，无第三方 UI 框架

## 许可证

[MIT](LICENSE) © 2026 KukaNo996

## 免责声明

- 本项目仅用于个人学习与本地硬件监控。
- 读取温度、风扇、功耗需要加载第三方内核驱动，可能被杀毒软件或游戏反作弊标记；
  由此产生的任何后果由使用者自行承担。
- 热搜与天气数据来自第三方公开接口，版权归各平台所有，本项目不存储、不分发其内容。
