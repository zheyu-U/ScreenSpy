# ScreenSpy

常驻 Windows 的自用**电脑使用时长追踪器**。回答一个问题：*我今天到底在电脑上花了多久、都花在哪了。*

它只统计，不干预：**不锁屏、不拦截、不强制关闭**任何程序。

---

## 功能

| 功能 | 说明 |
|------|------|
| **真实活跃时长** | 只累加"人在电脑前"的时间：挂机（默认 5 分钟无输入）、锁屏/睡眠、时间空洞一律不计入 |
| **按软件分别统计** | 软件身份 = 归一键（进程名小写）；支持**别名合并**（`code` → `vscode`）与**自定义显示名** |
| **分类** | 给软件指定分类（单选，保证"各分类之和 == 各软件之和"） |
| **桌面卡片** | 半透明深色极简卡片，两种形态可切换：**嵌入桌面层**（鼠标穿透、会被其它窗口盖住）或**浮动窗口**（可拖动、位置持久化） |
| **限额提醒** | 总限额 / 单软件限额 / 分类限额；阈值可配置（默认 80% / 100%），到点弹托盘气泡，去重只弹一次 |
| **近 7 天柱状图** | 维度可切：每天总量 / 某个软件 / 某个分类 |
| **托盘常驻** | 关闭主窗口仅隐藏；开机自启（可选）时只进托盘、不弹界面 |

## 技术栈

- **C# / .NET 10**（`net10.0-windows`），单工程 `ScreenSpy.csproj`
- **WPF** 主界面 + **WinForms `NotifyIcon`** 托盘
- **手写 Win32 顶层分层窗口 + GDI+** 绘制桌面卡片（`UpdateLayeredWindow` 逐像素 alpha）
- **SQLite**（`Microsoft.Data.Sqlite`）持久化，WAL 模式
- **原始活动日志**为 JSONL（未规范化，保留进程名/类名/标题，用于追溯）

## 构建与运行

```powershell
# 构建
dotnet build ScreenSpy/ScreenSpy.csproj -c Debug

# 运行（不带参数即正式形态）
dotnet run --project ScreenSpy/ScreenSpy.csproj
```

> 若构建报 `MSB3021/MSB3027`（文件被占用），说明托盘里已有 ScreenSpy 在运行：
> 先从托盘右键**退出**，再构建。

### 命令行开关（均为可选，只用于覆盖默认值）

```
--data-dir=<目录>       库放到 <目录>\data.db
--db=<文件>             直接指定库文件
--log-dir=<目录>        原始日志目录
--no-store              关闭 SQLite 落库
--no-raw-log            关闭原始活动日志
--no-card               启动时不显示桌面卡片
--autorun               由开机自启拉起：只进托盘，不显示主界面
--idle-threshold=<秒>   空闲阈值（默认 300）
--heartbeat=<毫秒>      心跳间隔（默认 1000，最小 100）
--flush-ms=<毫秒>       落库间隔（默认 15000，最小 1000）
--probe-ms=<毫秒>       会话（锁屏）探测间隔（默认 5000；0 = 关闭周期探测）
```

未知参数**不会**被静默忽略，而是在主界面显示为警告。

> 开发期建议用隔离的数据目录，避免污染正式数据：
> `dotnet run --project ScreenSpy/ScreenSpy.csproj -- --data-dir=artifacts/manual`

## 自检

每个里程碑都有内嵌自检入口（控制台打印，跑完即退出；退出码 0 = 全部通过）。
**这是本项目的主要回归手段**，不依赖测试工程。

```powershell
$exe = "ScreenSpy\bin\Debug\net10.0-windows\ScreenSpy.exe"

& $exe --m1-selfcheck    --logic-only   # 计时调度器 + 空闲检测
& $exe --m2-selfcheck    --logic-only   # 锁屏/睡眠监听（--lock-test / --sleep-test 会真锁屏/真睡眠）
& $exe --m3-selfcheck    --logic-only   # 前台进程识别 + 分类
& $exe --m4-selfcheck    --logic-only   # SQLite 存储 + 批量 flush + 重启续算
& $exe --m5-selfcheck                   # 托盘图标与菜单
& $exe --m6-selfcheck                   # 卡片渲染与数据映射
& $exe --m7-selfcheck                   # 卡片双形态 + 位置持久化
& $exe --m9-selfcheck                   # 软件身份层 / 自定义名称 / 限额引擎
& $exe --m10-selfcheck                  # 近 7 天图表
& $exe --m11-selfcheck                  # UWP 应用名修正
& $exe --m12-selfcheck                  # 开机自启（在真实 HKCU 临时键上做往返）

# 其他诊断入口
& $exe --demo-card      # 桌面卡片嵌入形态 + 截屏像素判定（M0 结论的可复现验证）
& $exe --diag-uwp       # 打印真实 UWP 窗口关系，用于排查 UWP 识别
```

自检输出同时写入 `artifacts/<里程碑>/`。

## 数据位置

| 内容 | 位置 |
|------|------|
| 聚合库（SQLite，WAL） | `%LOCALAPPDATA%\ScreenSpy\data.db` |
| 原始活动日志（JSONL） | `%LOCALAPPDATA%\ScreenSpy\logs\app-activity-YYYYMMDD.jsonl`（保留 30 天） |
| 开机自启注册表项 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `ScreenSpy` |

**统计口径**（四项守恒，用于对账）：

```
已归因软件 + 不计入使用时长（桌面/外壳/自身） + 无前台·未知 + 锁屏剔除 == 被计入的活跃
```

「今日真实活跃」= 本次运行 − 锁屏剔除 + 库中基线；主界面 / 卡片 / 托盘三处同源。

## 文档

`docs/` 下按"规范 + 证据链"组织（规范见 `开发文档.md`，各里程碑验证结论见 `M*-验证.md`）：

- `开发文档.md` —— 需求、设计、口径、里程碑（**唯一的规范来源**）
- `对话纪要.md` —— 已确认决策（结论层）
- `UI-重写思路.md` / `核心接口说明.md` / `未来计划.md` —— **UI 重写与后续改造**：渲染引擎（SkiaSharp）、常驻核心 + 按需 UI 进程、接口面清单、M13–M17 分期
- `对话流程记录.md` —— 逐轮过程与决策日志（D1–D90）
- `阶段总结.md` —— 按阶段沉淀的总结与教训
- `1005.md` —— 会话压缩摘要（"看完就能接手"的入口）
- `开发流程与注意事项.md` —— 标准开发循环、验证纪律、注意事项分类、25 条教训索引（**动手前必读**）
- `M0验证结论.md` / `M0-B+验证.md` / `M0-问题与解决.md` —— 桌面嵌入的生死判定
- `M1-验证.md` … `M12-验证.md` —— 各里程碑的交付物、自检、变异测试、已知局限
