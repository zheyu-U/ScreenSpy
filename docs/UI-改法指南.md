# UI 修改入口指南

> 目的：想改界面（观感 / 排版 / 布局 / 交互）时，**改哪个文件、别动哪个、怎么验**。
> 读者：接手本项目的开发者。动手前请先读 `开发流程与注意事项(M0-M12).md`（验证纪律）。
> 相关文档：`外观升级-验证.md`（卡片现观感是怎么来的、实测数字）、`M13-验证.md`（渲染引擎迁移）、`UI-重写思路.md`（分层与未来规划）。

---

## 一、先分清：这里有 4 块"UI"，互不相干

| 界面 | 技术 | 主要文件 | 改动的风险 |
|---|---|---|---|
| **桌面卡片** | SkiaSharp 手绘 + Win32 分层窗口 | `Rendering/*`（+ 窗口层 `Widget`、`Desktop`） | 中（有像素级自检兜着） |
| **主窗口** | WPF XAML | `MainWindow.xaml` / `MainWindow.xaml.cs` | 低 |
| **近 7 天图表** | WPF 代码建控件树 | `Charts/BarChartView.cs`、`Analytics/BarChartLayout.cs` | 低 |
| **托盘** | WinForms `NotifyIcon` | `AppHost/TrayIconHost.cs`、`AppHost/TrayIconFactory.cs` | 低 |

下面的 §二、§三 是重点（卡片是唯一"改错不会报错、只会看着怪"的一块）。

---

## 二、改桌面卡片：**外观只在这 4 个文件里**

> 卡片外观的唯一来源已收敛成三个常量类 + 一个渲染器。**改观感 99% 不需要碰窗口层。**

| 你想改什么 | 改哪里 | 具体位置 |
|---|---|---|
| **配色、投影** | `Rendering/CardTheme.cs` | `BodyTop/BodyBottom/BodyAlpha`（底板渐变）、`White/Dim`（文字）、`Accent/AccentLight/Danger`（强调/超限）、`TrackBackground/Divider`、`GlowTop/GlowFade/GlowSpanRatio`（顶部辉光）、`EdgeHighlight`（边缘高光）、`ShadowColor/ShadowOffsetY/ShadowSigma`（投影） |
| **卡片尺寸、圆角、投影边距** | `Rendering/CardGeometry.cs` | `CardWidth`、`CardHeight`、`Radius`、`ShadowMargin`。**`WindowWidth/WindowHeight` 是从它们算出来的，别手改** |
| **排版：间距、字号、行数** | `Rendering/CardLayout.cs` | `Pad`、`TitleTop`、`TitleToTotal`、`TotalToBar`、`BarHeight`、`BarToLimit`、`LimitToCurrent`、`CurrentToDivider`、`DividerToRows`、`RowStep`、`RowBarOffset`、`RowBarHeight`、`TrailerStep`、`FontTitle/FontTotal/FontMid/FontSmall`、`MaxRows/MaxTrailers` |
| **绘制本身：新元素、绘制顺序、对齐方式** | `Rendering/SkiaCardRenderer.cs` | `DrawContent()`（整幅排版的先后与位置）、`DrawBody()`（底板/辉光/描边）、`DrawShadow()`（投影）、`DrawBar()`（进度条/迷你条）、`DrawText()` + `Baseline()`（文字定位）、`FontFallback`（字体回退链） |
| **卡片上显示哪些数据** | `Widget/CardData.cs` | `FromStatus()`（快照 → 卡片模型）、`MaxRows`（显示几行） |
| **卡片数据字段（增删一行内容）** | `Rendering/CardModel.cs` | 纯数据类，加字段即可 |

### 改这些的 4 个坑（都是实测踩出来的）

1. **卡片 ≠ 窗口。** 外侧投影需要卡片以外的像素，所以：
   `窗口 = 卡片 + ShadowMargin × 2`。改 `CardWidth/CardHeight` 不用改别处（窗口尺寸自动跟随），
   但**不要**去手改 `WindowWidth/WindowHeight`；渲染器在构造时会**硬校验**尺寸，不一致直接抛异常。
2. **Skia 的画笔 alpha 会乘到着色器输出上。** 用渐变必须走 `UseShader()`（它先把画笔置为不透明白），
   否则上一步绘制残留的 alpha 会把颜色整体压暗 —— 症状只是"颜色有点脏"，**不像 bug**。
3. **文字位置靠 `Baseline()` + `TextTopAdjust`。** `CardLayout` 里的 y 是"行框上沿"，肉眼看到的是**墨迹顶边**，
   两者差一个随字号变化的空白。**改字号后墨迹位置会变**，需要重新标定（见 §四）。
4. **中文靠 `FontFallback` 前两项。** 顺序（`Microsoft YaHei UI → Microsoft YaHei → Segoe UI → Tahoma`）不要动，
   掉到 Segoe UI 就开始掉字。

---

## 三、**不要动**的地方（红线）

| 文件 | 为什么 |
|---|---|
| `Widget/CardWindow.cs` | 只管**窗口行为**：形态（嵌入/浮动）、鼠标穿透、命中测试、z 序自锁、STA 线程、重绘节奏。这些是 M0 用截屏像素换来的结论。**只有**要改默认位置 `DefaultX/DefaultY` 时才碰它（`DefaultWidth/Height` 是从几何常量派生的，不该改） |
| `Desktop/NativeWindowHost.cs`、`Desktop/DesktopZOrderManager.cs` | 同上，窗口层实现。整窗命中测试（`HTTRANSPARENT`/`HTCAPTION`），与圆角无关，改外观不需要动 |
| `Rendering/CardMarkers.cs` 的 `MarkerColor` | 它是**全部像素判定的基准**（截屏反查卡片位置靠它）。改它等于废掉全部客观验证。文件里已写明"不要改" |
| `Rendering/ICardRenderer.cs`、`CardRendererFactory.cs` | 这是接缝与"引擎唯一选择点"。**`CardWindow` 必须不认识 Skia** —— 这是 M15 拆 UI 进程的前提。不要为了改外观把引擎细节漏进窗口层 |

> 一句话：**"窗口层冻结"指的是行为冻结**。改外观时 `CardWindow.Draw()` 里那几行接线以外的东西一行都不该动。

---

## 四、改完怎么验（三步，都是现成入口）

```powershell
# 1) 构建：必须 0 错误 0 警告
dotnet build ScreenSpy/ScreenSpy.csproj

# 2) 像素级自检（50 项，退出码 0 = 全通过；顺带导出 PNG）
& ScreenSpy\bin\Debug\net10.0-windows\ScreenSpy.exe --m13-selfcheck
#    离线看图（已做"预乘 → 直通"转换，可直接人眼评审）：
#    artifacts/m13/card-full.png

# 3) 真机看卡片（可选 --win-d 验证按 Win+D 后不被最小化）
& ScreenSpy\bin\Debug\net10.0-windows\ScreenSpy.exe --demo-card
& ScreenSpy\bin\Debug\net10.0-windows\ScreenSpy.exe --demo-card --win-d
#    可用参数：--x= --y= --no-marker --out= --name= --settle-ms=
```

**自检红了怎么读**：

| 红的组 | 含义 | 怎么办 |
|---|---|---|
| **A 像素语义** | 预乘 / 底板 alpha / 圆角 / 投影边距 / 缓冲是否写满 | 多半是动了 `CardTheme`/`CardGeometry` |
| **B 外观不变量** | 投影、渐变、进度条、**墨迹锚点**、最坏情形是否溢出 | 改了排版/字号后**需要重新标定**（见下） |
| **C 接缝与契约** | 渲染器单点、入参校验、尺寸一致性、`Dispose` 幂等 | 多半动了接口或工厂 |
| **D 窗口层回归** | 样式位、命中测试、持续出帧、截屏上屏判定 | **说明误动了窗口层 —— 回退** |

**改外观后需要同步的两类"钉死的期望值"**（否则自检会挂，且那是正确行为）：

* `Demo/M13SelfCheck.cs` 里的 `TitleInkTop` / `TotalInkTop`：墨迹顶边的**实测字面量**（零容差）。
  **换机器或换字体**导致它们失败时，先看图确认是否真的变丑，再重新标定这两个数字。
  刻意留着零容差，是为了让"把 `TextTopAdjust` 改坏 1px"也能被抓到。
* `B6` / `B6c`：内容不得溢出卡片底边；`CardData.MaxRows`（显示几行）≤ `CardLayout.MaxRows`（装得下几行）。

**纪律（本项目的铁律）**：改完关键不变量后，把它**故意改坏**跑一次自检，必须**恰好命中预期项数** ——
否则说明那条断言是空的。（`外观升级-验证.md` §六 有三轮变异测试的范例。）

**自检验不到的**：卡片好不好看、拖动的手感、被别的窗口盖住的观感 —— 这些**只能人眼验收**。

---

## 五、常见任务配方

* **换强调色 / 让配色更暖**：改 `CardTheme.Accent` / `AccentLight`（超限色 `Danger`）。
  改完跑自检：`A1/A2`（预乘与底板 alpha）与 `B2b` 会核对着色。
* **卡片变宽 / 变高**：只改 `CardGeometry.CardWidth` / `CardHeight`。
  变矮要留意 `B6`（最坏情形溢出）；圆角上限是 `min(宽, 高) / 2`，超了 Skia 会静默收缩而 `A3b` 会抓到。
* **圆角更圆**：改 `CardGeometry.Radius`。
* **加一行内容**：① `CardModel` 加字段 → ② `CardData.FromStatus` 填值 → ③ `CardLayout` 加间距常量并重排
  → ④ `SkiaCardRenderer.DrawContent` 画出来 → ⑤ 自检加一条锚点（别只靠肉眼）。
* **改投影更强**：`CardTheme.ShadowSigma` / `ShadowColor`。**变大就要同步调大** `CardGeometry.ShadowMargin`，
  否则投影会在窗口边缘被切断（`A6` 会绊住）。
* **改卡片默认位置**：`CardWindow.DefaultX/DefaultY`（注意这是**窗口**左上角，卡片本体差一个 `ShadowMargin`）。

---

## 六、其它三块 UI

* **主窗口布局**：`MainWindow.xaml`。它是一整列 `StackPanel`，按里程碑分节（今日活跃 / 卡片形态 / 软件与分类 / 限额 / 近 7 天 / 开机自启 …），
  各节都有 `x:Name`，文案与控件都在这里。
* **主窗口逻辑**：`MainWindow.xaml.cs`。**注意两种刷新节奏**：
  纯文本状态**每秒**刷新；含交互控件的节（下拉框、文本框、软件行、限额行）**按需重建**（进窗口 / 点刷新 / 改动之后）——
  **不要把交互控件放进每秒刷新里**，那会把用户正打开的下拉框销毁掉。
* **图表**：几何（纯函数、可断言）在 `Analytics/BarChartLayout.cs`；绘制（摆控件）在 `Charts/BarChartView.cs`。
  尺寸与配色在 `BarChartView`（`PlotHeight`、`BarWidth` 与几个 `Brush`）。**换 UI 只需替换绘制层**，几何规则保留。
* **托盘**：菜单项与图标在 `AppHost/TrayIconHost.cs`，图标本体在 `AppHost/TrayIconFactory.cs`。

---

## 七、动手前请先确认的一处文档漂移

`CardGeometry.CardHeight` 当前是 **410**，而 `CardLayout.cs` 的注释与 `外观升级-验证.md` 里仍写着 **400**
（同文件那句注释还写着"比 360 高 40"，对不上 410）。**以代码为准**，但改尺寸前建议先把这几处数字统一 ——
本项目最忌讳"文档说 X、代码实际 Y"。

---

## 八、改 UI 的标准流程（照抄即可）

1. 定位上面 §二 的表 → 只改那一个文件（需要时同步 `M13SelfCheck` 的期望值）。
2. `dotnet build`：0 错误 0 警告。
3. `--m13-selfcheck`：50 项全过；红了按 §四 的表定位。
4. 变异测试：把刚改的关键值故意改坏，确认**恰好命中预期项数**，再复原。
5. `--demo-card`（+ `--win-d`）看真机；人眼评审 `artifacts/m13/card-full.png`。
6. 同步文档：在 `外观升级-验证.md` 追加一节（新规格 + 实测数字 + 已知取舍），改了尺寸/口径要同步 `开发文档.md`。
