# UI 重写思路（分析与决策）

> 本文回答：**主界面与卡片界面要大面积重写/优化时，应该怎么走、先做什么、风险在哪。**
> 与既有文档的分工：`开发文档.md` 是规范，`M*-验证.md` 是证据，`阶段总结.md` 是时间线，
> `开发流程与注意事项.md` 是纪律汇总；**本文是"面向未来改造"的专门分析**，配套两份：
> `核心接口说明.md`（给 UI 开发者的接口面清单）与 `未来计划.md`（M13–M17 分期）。

---

## 一、结论摘要

1. **重做 UI 的成本比一般项目低**：采集 / 口径 / 存储 / 限额 / 图表几何早已被抽到 UI 之外，
   它们与 UI 的接口是**一份只读快照（`RuntimeStatus`）+ 一组命令**。
2. **但有一个必须先填的坑**：自检把断言**锚在 `MainWindow` 的静态成员上**
   （`ComposeNonAppTopRow` / `ComposeChartScope` / `BuildAutoStartHint` …）。
   直接重写主界面 = 把验证网一起删掉。**所以第一步是"抽象表现层"，不是"换框架"。**
3. **主界面不值得为"好看"换框架**：它的痛点是**信息架构**（十来个区块堆成一条长滚动），
   换框架解决不了信息架构。优先 **框架内现代化（导航 + MVVM + 主题库）**。
4. **卡片必须分三层看**：
   - 窗口层（`CardWindow` / `NativeWindowHost` / `DesktopZOrderManager`）→ 🔒 **冻结不动**
     （M0 用截屏像素判定换来的结论，任何 UI 框架都替代不了）；
   - 渲染层（`CardRenderer`，现 GDI+）→ 🔄 **换 SkiaSharp**（本文第五节）；
   - 数据层（`CardModel` / `CardData`）→ 🔒 **契约不变**（换渲染层的接缝就在这里）。

---

## 二、现状：UI 层到底是什么

| 层 | 文件 | 体量 | 性质 |
|---|---|---|---|
| 主界面布局 | `MainWindow.xaml` | 约 10 KB / 140 行 | **一个 `ScrollViewer` + `StackPanel`**，十来个区块纵向堆叠；无样式、无资源字典、无模板、无导航 |
| 主界面逻辑 | `MainWindow.xaml.cs` | **约 60 KB** | UI 构造 + 全部逻辑都在 code-behind：动态行**命令式新建**（`BuildAppRow` / `BuildLimitRow` 手搓 `StackPanel` + `Thickness`）、每行事件回调、对话框、图表装配 |
| 图表绘制 | `Charts/BarChartView.cs` | 约 6 KB | WPF 控件树摆放，**刻意做薄** |
| 图表模型 / 几何 | `Analytics/*` | 约 17 KB | **纯逻辑**（口径 / 柱高 / 范围查询），与 UI 无关 |
| 卡片数据 | `Widget/CardData.cs` | 薄 | `RuntimeStatus` → `CardModel` 的**纯映射** |
| 卡片渲染 | `Rendering/*` | 约 13 KB | **GDI+ / `System.Drawing`**，逐像素画进 32bpp 预乘 ARGB 位图 |
| 卡片窗口 | `Widget/CardWindow` + `Desktop/NativeWindowHost` + `DesktopZOrderManager` | 约 25 KB | **纯 Win32 互操作**（分层窗口、`UpdateLayeredWindow`、z 序自锁、显示桌面复位） |

**一句话**：主界面是"**一个大长面板 + 60 KB code-behind**"，卡片是"**Win32 窗口 + GDI+ 手绘**"。
两者唯一的共同点是：**数据都来自同一个 `RuntimeStatus` 快照**。

**已经很好的部分（不要动）**：
- 数据与展示完全分离（`RuntimeStatus` 是只读 DTO，`Snapshot()` 永不抛异常）；
- 卡片窗口层是 M0 客观验证过的 B+ 形态；
- 图表已分三层（查询 / 口径 / 几何 是纯逻辑，只有一层碰 WPF 视觉）。

---

## 三、约束：验证网与 UI 的耦合（实测确认）

自检**直接调用 `MainWindow` 的静态成员**：

| 自检 | 引用的 `MainWindow` 成员 |
|---|---|
| `M6` | `ComposeNonAppTopRow` / `NonAppRowName` / `ComposeAppTopRow` / `ShareCellWidth` |
| `M7` | `ComposePositionControlAvailability` / `BuildPositionHint` |
| `M10` | `ComposeChartScope` / `BuildChartDimensionChoices` / `ChartTotalLabel` / `ComposeChartHint` |
| `M12` | `BuildAutoStartHint` |

这些函数的**函数体是纯的**（只吃 DTO、只返回字符串/枚举），但**宿主是 WPF 的 `Window` 类**。后果：

> 一旦重写 `MainWindow.xaml.cs`，这些断言会**跟着消失** —— 不是"测试失败"，而是
> **根本编译不过、或被顺手删掉**。而它们恰好覆盖"今日口径 / 不计入统计行 / 图表维度键 /
> 自启四态文案"这些**已经栽过的坑**。

**这是本项目最大的资产（验证网）与 UI 之间唯一的强耦合点，也是全部计划的起点。**

---

## 四、内存事实（决定架构，而不是靠感觉）

核对了启动路径与生成入口（`obj\...\App.g.cs`）：

```csharp
[STAThread]
public static void Main() {
    ScreenSpy.App app = new ScreenSpy.App();   // ← App : System.Windows.Application
    app.Run();
}
```

| 事实 | 出处 | 影响 |
|---|---|---|
| **WPF 被无条件创建** | 生成入口 `App.g.cs` | 跑任何东西（含纯控制台自检 `--m1-selfcheck`、`--diag-uwp`）都会加载 WPF |
| **`MainWindow` 被无条件构造** | `App.xaml.cs:178`（`new MainWindow(_runtime)`） | `--autorun`（只进托盘）时，**整棵视觉树已被实例化**，用户可能几小时都不打开它 → 纯浪费 |
| 主窗口的 `DispatcherTimer` **按需启动** | `Loaded` 里 `Start`、`Closed` 里 `Stop` | ✅ 这项设计是对的，CPU 不浪费 |
| **WinForms 也被加载** | `UseWindowsForms=true` | 只为 `NotifyIcon` + `System.Drawing`（截屏取像素） |

**两条直接可行的"静默内存"改进（不依赖任何框架决策）**：
1. **主窗口惰性创建**：首次显示时才 `new MainWindow(...)`；
2. （可选）托盘改 `Shell_NotifyIcon` P/Invoke、截屏改 BitBlt → 核心不再需要 WinForms 程序集。

**但必须说清一条硬约束**：
> **XAML 框架的加载是进程级且不可卸载的。** 只要 WPF（或 WinUI 3）被创建过，它的程序集、
> Dispatcher、主题/字体缓存就一直占着，**关掉窗口也收不回来**。
> 所以"静默时不渲染主窗口"这件事，**单进程里无论怎么写都做不到** ——
> 最多只能做到"不创建窗口对象"（省视觉树），**省不掉框架地板**。
> **进程边界是唯一能让操作系统把内存完整收回来的手段。**

---

## 五、卡片渲染：GDI+ → SkiaSharp

### 5.1 现状管线

```
LayeredSurface 构造：GDI+ Bitmap(32bppPArgb) + Graphics
CardRenderer.Draw(Graphics, size, model)      ← 手绘：矩形/圆角/文字/进度条
   → Present(hwnd, x, y):
        Graphics.Flush(Sync)
        LockBits → 逐行 MemoryCopy 到 DIB(_bits)     ← 每帧一次全帧拷贝
        UpdateLayeredWindow(AC_SRC_ALPHA)
```

### 5.2 SkiaSharp 是什么、与 GDI+ 的差别

**SkiaSharp 是 Google Skia 的 .NET 绑定** —— Chrome / Android / Flutter 用的那个 2D 引擎。
拿到的是一个 `SKCanvas`（立即模式绘制），**与 `System.Drawing` 是同一层级的东西**：

| | GDI+（现在） | SkiaSharp |
|---|---|---|
| 出身 | Windows 图形栈的老封装 | Chrome/Android 引擎，跨平台 |
| 效果能力 | 圆角/渐变可做；**模糊、投影、混合模式基本没有** | 阴影/模糊/渐变/混合模式**都是一等公民** |
| 光栅化 | GDI+ 自己的（ClearType、hinting） | Skia 自己的（更接近"所见即所得"） |
| 依赖 | **零依赖**（.NET 自带） | **一个 native 库**（`libSkiaSharp.dll`，磁盘十几 MB） |
| 内存 | — | **净增**（不是减少） |
| 跨框架复用 | 否 | **是**（Avalonia 的绘制引擎就是 Skia） |

**一句话**：换 Skia 得到的是**能力**，不是性能、更不是内存。

### 5.3 先纠正一个常见误解：性能差异可忽略

按卡片的真实参数算：

| 量 | 值 |
|---|---|
| 卡片尺寸 | 400 × 360 = 144,000 像素 |
| 每帧缓冲 | × 4 字节 ≈ **0.55 MB** |
| 重绘频率 | `RedrawMs = 1000` → **最多 1 帧/秒** |
| 因此"逐行拷贝"的开销 | **约 0.55 MB/秒** |

现代内存带宽是几十 GB/秒 级别 → 这占约 **0.001%**。
**所以"省一次拷贝"不能作为选型理由**；真正的理由应该是**结构简洁度**与**可调试性**。

### 5.4 三条写像素路径的优劣

选择点只有一个：**Skia 画在哪块内存上**。

#### (a) 直写 DIB

```csharp
var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace: null);
using SKSurface? surface = SKSurface.Create(info, _bits, w * 4);   // 直接包住 DIB
if (surface is null) { /* 必须回退 */ }
surface.Canvas.Clear(SKColors.Transparent);
```

**优势**
- **只有一块缓冲**：画完的像素**就是**要上传的像素 —— 真正的单一事实来源。
- 删掉 `_bitmap` / `_graphics` / 整个 `BlitToDib()`（约 35 行），`LayeredSurface` 从 157 行瘦到约 110 行。
- 没有中间 `Bitmap`（省约 0.55 MB 常驻 + 每帧一次锁定/分配）。

**风险（都是真的）**
- `SKSurface.Create` **可能返回 null**（Skia 认为该 `SKImageInfo` 不支持直写外部内存）→ **回退到哪？回退就是 (b)**。
- **颜色空间必须传 `null`**：若传 sRGB，Skia 会做色彩管理转换，**预乘 alpha 会被算错**（半透明底板偏色）。
- 该重载的**签名与可用性随版本变化，且未在本项目实测过** —— 把可靠性押在一个没取证的点上，正是 M0 栽过的坑。
- **不能用 GPU 后端**：需回读、反而慢，必须走 CPU 光栅。

#### (b) Skia 位图 → 拷进 DIB

```csharp
// 构造：把 GDI+ 的 Bitmap/Graphics 换成 Skia 的
_skiaBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, null));
_skiaCanvas = new SKCanvas(_skiaBitmap);          // ← 常规 API，不涉及外部内存

// BlitToDib：把 LockBits 换成取指针（比现在更简单：Skia stride 恒为 w*4）
int bytes = _width * _height * 4;
unsafe { Buffer.MemoryCopy((void*)_skiaBitmap.GetPixels(), (void*)_bits, bytes, bytes); }
```

**优势**
- **与现在已被 M0/M6 验证过的路径同构** —— 行为最容易预测；
- **`Present()` 整段原封不动**（`_dib` / `_bits` / `_memDc` / `_screenDc` / `UpdateLayeredWindow`，都是像素判定验证过的）；
- **可把 Skia 输出单独编码成 PNG 落盘** → 与 GDI+ 输出做**离线逐像素对照**；
- `new SKCanvas(SKBitmap)` 是 SkiaSharp 最常规用法，**不触碰"直写外部内存"这个不确定点**。

**劣势**
- 多一块缓冲 + 一次拷贝（可忽略，见 5.3）；
- 多一层"画错了还是拷错了"的归因问题。

#### (c) (a) 为主、(b) 为备

**优势**：回退分支本来就必须有；可 A/B 对照；"可证伪"符合本项目作风。

**劣势（这是不选它的原因）**
- 代码多 20–40 行并**不多**，但多出三样东西：两条路都要验、**静默掩盖风险**（(a) 悄悄走了 (b) 却以为在跑直写 —— 正是本项目最恨的"不报错的错"）、必须再加"当前路径"断言与自检装置。
- 而它换来的收益（省 0.001% 的开销）**不值得**。

### 5.5 决策：选 (b)

**理由**：
1. 单一路径、保留已验证的 `Present` 管线、不碰未取证的重载；
2. 与"像素等价迁移"的验收目标**天然契合**（能 dump PNG 做像素 diff）；
3. **(b) 是通往 (a) 的自然第一站** —— 先跑通、调好外观，之后再决定是否省那块缓冲
   （改动很小：把 `GetPixels()` 换成 `_bits`）。反过来先做 (a) 则要在"引擎换了"和
   "直写是否成立"两个未知里同时摸索。

**保留 GDI+ 渲染器作为回归基线**：决策 ④ 已定"核心允许保留 WinForms" →
`System.Drawing` 仍在进程里 → 现有 `CardRenderer` 可原样保留、继续编译、继续跑。

### 5.6 为什么 GDI+ 当年必须拷贝，而 Skia 能直写

这不是设计冗余，而是 GDI+ 的无奈：

> GDI+ 通过 HDC 往 DIB 上画时，**per-pixel alpha 会丢失**（GDI 的绘制语义不保证 alpha 通道）。
> 所以做法是：先画进一块 GDI+ 自己的 `Format32bppPArgb` 位图（alpha 完整），
> 再把**原始字节**拷进 DIB。

**Skia 的 CPU 光栅器是纯软件逐像素写内存**，输出就是精确的 BGRA + 预乘 alpha，
与 `UpdateLayeredWindow(AC_SRC_ALPHA)` 的要求逐字节一致。**这是"直写"能成立的根本原因。**

### 5.7 迁移工作量与坑（重点是别低估）

`CardRenderer.cs`（约 173 行）是**整段重写**：

| GDI+ | Skia | 备注 |
|---|---|---|
| `g.DrawString(...)` | `canvas.DrawText(...)` | 2.88 用 `SKPaint.TextSize`；**3.x 改用 `SKFont`，API 不兼容** → 版本必须钉死 |
| `g.MeasureString(...)` | `SKFont.MeasureText(...)` | **度量值不同**：GDI+ 会额外加 padding，Skia 更紧 |
| `GraphicsPath` + `FillPath/DrawPath` | `SKPath` + `canvas.DrawPath` | 圆角矩形可直接 `DrawRoundRect`，更短 |
| `SolidBrush` / `Pen` | `SKPaint` | 一个 paint 兼做填充/描边/文字 |
| `Font(UiFamily, 12.5f, Pixel)` | `SKTypeface.FromFamilyName(...)` | **字体回退链必须原样保留**（`Microsoft YaHei UI → Microsoft YaHei → Segoe UI → Tahoma`），否则中文掉字体 |

**最容易被低估的一项**：现在那些 `y += 24 / 48 / 30 / 22 / 17` 与右对齐 `w - pad - tw`
全是按 **GDI+ 字体度量**凑出来的。换引擎后行高/字宽都会变，**卡片一定错位，必须逐项重调**。

> 结论：**像素等价迁移 ≠ 零改动**，而是"外观目标不变，但要重新对齐一遍坐标"。

### 5.8 验收判据（客观，沿用 M0 的作风）

| 要验证的 | 判据 |
|---|---|
| ① 直写 / 拷贝的像素正确性 | 读 DIB 字节核对 BGRA 与预乘值（50% 白 → `128,255,255,255`） |
| ② 颜色空间传 `null` 时半透明不被改色 | 同上 |
| ③ 真的上屏 | **复用 M0 的不透明标记色块**做精确锚点 + 截屏判定 |
| ④ 与 GDI+ 的差异可归因 | 离线 PNG diff（结构/颜色/位置一致；字形边缘允许小幅差异） |

**先做 5 分钟 spike 取证，再迁移**（M0 教训：API 返回值全都"成功"、像素却从未上屏）。

---

## 六、"常驻核心 + 按需 UI 进程"

### 6.1 为什么需要进程边界

见第四节那条硬约束：**XAML 框架加载不可卸载**。
两进程后：
- 静默时**只有核心**，UI 框架一行都没加载；
- 打开主界面时，UI 进程自己扛那几十 MB；
- **关掉主界面 = 整个 UI 进程退出，内存完整归还操作系统**（不是"GC 慢慢回收"）。

**顺带的语义红利**：M5b 的"关闭窗口不退出"（`e.Cancel = true` + `Hide()`）**自然消失** ——
UI 进程关掉就是退出，核心还在托盘里。少一条特殊逻辑。

### 6.2 角色划分

| 关注点 | 归属 |
|---|---|
| 采集 / 口径 / 存储 / 限额 / 身份分类 | **核心** |
| 托盘图标、通知气泡 | **核心**（它才是永远活着的那个；限额提醒必须在主界面从未打开时也能弹） |
| **桌面卡片** | **核心**（卡片是产品本体，一直可见） |
| 开机自启注册表 | **核心**（单一写者） |
| 单实例互斥体 | **核心** |
| 主界面 | **UI 进程**（按需启动，关掉即整进程退出） |

### 6.3 关键设计：一份接口，两种实现

把 UI 对核心的调用面抽成 `IScreenSpyCore`，然后：

- **今天**：核心自己实现它，UI **同进程直调**（= 现在的行为，零变化）；
- **将来**：加一个 `PipeCoreClient : IScreenSpyCore`，走命名管道；
- **UI 的代码一行不改** —— 因为它是针对接口写的。

**这是"逐步搬离"的支点，也是为什么契约抽取（M14）必须排在主界面重写之前。**
接口面的完整清单见 `核心接口说明.md`。

### 6.4 IPC 选型

| 方案 | 评价 |
|---|---|
| **命名管道 + 行分隔 JSON** | ✅ **推荐**。快照只有几百字节、1 秒一次；命令是稀疏动作。零新依赖，协议解析是**纯逻辑 → 可确定性自检** |
| 共享内存 | ❌ 对该数据量属过度设计 |
| UI 直连 SQLite | ❌ 会让口径逻辑（身份层、图表聚合）在 UI 侧再来一份 = 双份漂移 |
| gRPC / HTTP | ⚠️ 依赖重、部署复杂，收益不足 |

要点：**核心是服务端（单写者）**；管道名带当前用户 SID 并设 ACL（仅本机、仅当前用户）；
协议带 **version 字段**（老 UI 配新核心要给出明确提示，而不是解析失败后瞎猜）。

### 6.5 生命周期与失败模式（必须逐条定）

| 场景 | 应有行为 |
|---|---|
| 托盘点「打开主界面」 | 核心确保 UI 进程存活（没跑就拉起），并带到前台 |
| 用户直接启动 UI，但核心没跑 | 明确提示「核心未运行」+ 提供「启动」；**不要静默失败** |
| UI 崩溃 | 核心无感；下次打开重启一个 |
| 核心退出/崩溃（UI 还开着） | UI 显示「已与核心断开」并提供重连或退出；**不能卡死** |
| 协议版本不匹配 | 明确报"版本不一致"，不要试图兼容解析 |

### 6.6 四个坑（本项目特有）

1. **核心必须"零 WPF"**：入口现为 `App : Application`，WPF 无条件加载。
   **具体障碍**：`App.OnStartup` 里的 `MessageBox.Show(...)` 是 **WPF 的**，会立刻把 WPF 拉回来 →
   需换成托盘气泡 / WinForms MessageBox / TaskDialog。
2. **消息泵归属**：现在整个进程的消息泵是 WPF `Dispatcher`，托盘 `NotifyIcon` 借它收消息。
   核心去掉 WPF 后**必须自建消息循环**（隐藏窗口 + `GetMessage/DispatchMessage`，或 WinForms `Application.Run()`）。
   **不解决这条，托盘图标会"显示但点不动"。**
3. **12 个自检入口挂在 `App.OnStartup` 上**，要搬到"核心引导"里
   （它们本来就不需要 WPF —— 只有 M6/M7/M10/M12 的几条断言引用了 `MainWindow` 的纯函数，
   那正是契约抽取要搬走的东西）。
4. **单实例语义变化**：互斥体归核心；UI 自己"只有一个主窗口"要另外定。

### 6.7 单 exe 双角色 vs 拆工程 → 决策：拆工程

| 方案 | 优点 | 代价 |
|---|---|---|
| 单 exe 双角色（`ScreenSpy.exe` / `--ui`） | 部署简单（还是一个文件）；.NET 按程序集惰性加载，核心只要不触碰 WPF 类型就真的不会加载 | **无法在编译期阻止**"核心不小心引用了 WPF"——这类错误往往要到"内存怎么又高了"才发现 |
| **拆工程**（`Contracts` + `Core` + `UI`） | **编译期强制**核心零 UI 依赖；UI 换框架时改动被彻底关在一个工程内 | 多一个工程与发布步骤 |

**分两步走（降风险）**：
1. **M14**：先在单工程里用**命名空间**划边界（`Contracts` / `Presentation`），契约显式化；
2. **M15**：UI 真要进程化时再**物理拆开**，拆分是机械的。

---

## 七、主界面框架选择（含 WinUI 3 的看法）

| 方案 | 得到什么 | 代价 | 评价 |
|---|---|---|---|
| **WPF + MVVM + 现代主题库**（WPF UI / HandyControl / Fluent 主题） | 立刻现代化：导航、卡片式布局、深色主题、动画过渡；**零框架迁移** | 学习主题库约定；code-behind 改绑定 | ✅ **性价比最高** |
| **WinUI 3**（Windows App SDK） | 原生 Win11 Fluent、亚克力、最佳 DPI/无障碍 | 打包/部署模型变更、无设计器、与托盘/自绘窗口互操作要额外胶水、**XAML 框架地板更高**，且**托盘没有第一方 `NotifyIcon`**（还得靠 `H.NotifyIcon` 或 WinForms） | ⚠️ 只在两进程架构下才合适 |
| **Avalonia** | 跨平台、Skia 渲染、样式系统更现代；**卡片渲染可统一到 Skia** | Windows 特有物（托盘/注册表/钩子/分层窗口）都要自己接 | ⚠️ 若打算跨平台或统一渲染，值得 |
| **WebView2 + 本地 HTML/CSS** | 最容易做好看、动效最强、迭代最快 | 主界面变成"网页 + 桥"，内存与启动开销上升 | ⚠️ 主界面按需打开，开销可接受；但要接受桥接层 |

**判断门槛（满足任意一条再换框架，否则留在 WPF）**：
1. 需要 **WPF 确实做不到的能力**（真正的 GPU 复合动效、跨平台）；
2. 主界面要变成**长期打开、频繁交互**的应用；
3. 要把**卡片渲染与主界面统一到同一套绘制引擎**（→ Avalonia + Skia）；
4. 能接受**自检宿主、部署方式、托盘/互操作胶水**都要重做一遍。

> **关键**：**在 M15（进程化）之前，框架选择不是自由选项**——它的基线重量会成为核心的问题。
> 进程化之后，换框架只影响一个进程。

---

## 八、决策汇总

| 编号 | 决策 | 由谁定 |
|---|---|---|
| **D84** | 卡片渲染引擎换 **SkiaSharp** | 用户 |
| **D85** | 本轮**只做像素等价迁移**（外观不变，先换引擎） | 用户 |
| **D86** | 进程形态采用**拆工程**（`Contracts` / `Core` / `UI`） | 用户 |
| **D87** | **核心允许保留 WinForms**（不强制"零 UI 栈"，只保证不加载 WPF） | 用户 |
| **D88** | 渲染路径取 **(b)**：Skia 位图 → 拷进 DIB（不选 (a) 直写 / (c) 双路） | 本文分析 |
| **D89** | 先做 **5 分钟 spike 取证**再迁移（沿用 M0 作风） | 本文分析 |
| **D90** | 主界面优先 **框架内现代化**，换框架需先满足第七节门槛 | 本文建议 |

---

## 九、风险清单（本项目特有）

| 风险 | 说明 | 对策 |
|---|---|---|
| **验证网被删** | 自检锚定 `MainWindow` 静态成员 | 先做契约抽取；**逐页迁移**而非整体替换 |
| **"每秒全量重建"变成绑定风暴** | 现在 `Refresh()` 每秒 `Items.Clear()+Add` | 表现层先产出"稳定键 + 增量差异"，UI 只管渲染 |
| **动态控件与刷新互踩** | 现在靠 `_identityUiBusy` / `_chartUiBusy` 手动抑制 `SelectionChanged` | MVVM 后由绑定取代；**别再引入新的"手动抑制标志"** |
| **卡片窗口层被"顺手现代化"** | 最容易犯的错：换了 UI 库后觉得"顺便把卡片也用新框架画" | 硬约束：**卡片窗口层只允许 B+ 一种实现** |
| **对话框各自为政** | `AppRenameDialog` / `LimitEditDialog` 手写 WPF 模态窗 | 重写时统一成一种对话框服务 |
| **自检入口宿主** | 12 个入口内嵌在同一个 exe（`Demo/*SelfCheck.cs`） | 保留"app 即宿主"，否则入口全要重新挂载 |
| **Skia 字体度量变化** | 布局常量按 GDI+ 凑的 | 保留 GDI+ 渲染器作基线 + PNG diff 重调 |
| **"静默内存改善"期望错位** | Skia 不省内存、WinUI 3 反而抬高地板 | 立 `--mem-probe` 客观判据；靠**架构**而非渲染器 |

---

## 十、下一步

按 `未来计划.md` 执行：**M13（Skia spike → 迁移）→ M14（契约抽取 + mem-probe）
→ M15（UI 进程化/拆工程）→ M16（主界面重写，用户主导）**。

**两条不可动摇的顺序约束**：
1. **契约抽取必须早于主界面重写**（否则验证网被删）；
2. **进程化必须早于换框架**（否则框架地板重量成为核心的问题）。
