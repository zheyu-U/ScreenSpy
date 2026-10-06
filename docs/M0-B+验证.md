# M0 · 路线 B+（Rainmeter 式伪嵌入 + Win+D 免疫）客观验证

- 运行时间：2026-10-05 17:31:30
- 卡片位置：(200,700)，尺寸 400×360
- 判定依据：截屏中“不透明标记色 #FF00AA”的像素数（>200 视为可见）
- 截图目录：`D:\project\ScreenSpy\artifacts\m0-spike`（原 `Spike\artifacts`；Spike 工程已于 2026-10-05 删除，证据图移至此处）

## 结果表

| 步骤 | 说明 | 标记像素 | 遮挡色像素 | IsIconic | ShowDesktop | Elevated | z(Host/Probe/Card) | 该点最上层外部窗口(z) |
|---|---|---:|---:|---|---|---|---|---|
| S1 | 基线（按下 Win+D 之前） | 576 | 0 | False | False | False | 161/159/160 | 类=Windows.UI.Core.CoreWindow 标题="设置" z=103 |
| S2 | 已按 Win+D（显示桌面） | 576 | 0 | False | True | True | 15/161/14 | 类=Windows.UI.Core.CoreWindow 标题="设置" z=105 |
| S3 | 已再按 Win+D（还原） | 576 | 0 | False | False | False | 161/159/160 | 类=Windows.UI.Core.CoreWindow 标题="设置" z=103 |
| S4 | 不透明窗口压在卡片之上 | 0 | 119810 | False | False | False | 161/159/160 | 类=Windows.UI.Core.CoreWindow 标题="设置" z=103 |
| S5 | 遮挡已收起 | 576 | 0 | False | False | False | 161/159/160 | 类=Windows.UI.Core.CoreWindow 标题="设置" z=103 |

## 判定

- **Win+D 免疫成立**：S2（显示桌面状态下）标记像素 576 > 200，卡片在桌面上仍然可见。
- z 序实测（S2）：Host(Progman)=15，Probe=161，Card=14（下标 0 = 最前）。

### “显示桌面”判据的极性（本次实测校正）

- 正常态：Progman 位于 z 序**最后**（最底层），探针紧邻其前 ⇒ `HostZ > ProbeZ`。
- 显示桌面：系统把 Progman 抬到**最前**（实测下标约 17～22），探针仍留在底部 ⇒ `HostZ < ProbeZ`。
- 因此判据取 **`显示桌面 ⟺ HostZ < ProbeZ`**（桌面宿主跑到“压在底部的探针”之前）。
- 注意：这与“照字面理解 Rainmeter `FindWindowEx` 返回空即显示桌面”的直觉方向相反——极性必须按实测确定，不能照抄推断。

## 窗口属性与交互（客观读取，不依赖人眼）

- ExStyle 原值 = 0x080800A0（LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW）
- [通过] 鼠标穿透（机制）：含 WS_EX_TRANSPARENT。
- [通过] 鼠标穿透（功能）：标记点命中测试返回 SysListView32（桌面）—— 点击穿透到其下窗口。
- [佐证] 命中测试方法自检：同一坐标在 S4（不透明、无 WS_EX_TRANSPARENT 的对照窗口）返回该窗口自身 —— 说明上面“返回桌面”确因透明被跳过，而非命中测试失效。
- [通过] 任务栏/Alt+Tab 隐藏：含 WS_EX_TOOLWINDOW 且无 WS_EX_APPWINDOW（Windows 据此把该窗口排除在任务栏与 Alt+Tab 之外）。
- [通过] Win+D 还原：S3 标记像素=576、包围盒={X=210,Y=710,Width=24,Height=24}，与 S1 完全一致（无残影、无位移）。

## 备注

- 本验证会自动模拟两次 Win+D（可逆）。
- S1/S3/S5 若标记像素为 0，先看最后一列“该点最上层外部窗口(z)”：把它的 z 与同一行的 Card 值比较，z 更小即为“挡在卡片前面”，属预期（这是不嵌入方案的固有局限）；S2 才是决定性判据。
