using System;
using System.Globalization;
using ScreenSpy.Storage;

namespace ScreenSpy.Widget;

/// <summary>
/// 卡片形态（M7）。
///
/// 两种形态**是同一个窗口的两种参数组合**（单 HWND、单线程、共用一份坐标），
/// 因此切换不跳位、不闪断、也不需要同步两套位置。
/// </summary>
internal enum WidgetMode
{
    /// <summary>
    /// 嵌入桌面层（M0 验证过的 B+ 形态）：鼠标穿透、不抢焦点、z 序自锁 + 显示桌面复位。
    /// **语义是“压在桌面之上、其它普通窗口之下” —— 别的窗口会盖住它**（刻意要的行为）。
    /// </summary>
    Embedded = 0,

    /// <summary>
    /// 浮动普通窗口（M6 形态）：可点、可拖、可被激活。用于「调整位置」时把卡片叫到最前。
    /// </summary>
    Floating = 1,
}

/// <summary>卡片放置（形态 + 左上角屏幕坐标）。</summary>
internal readonly struct WidgetPlacement
{
    public WidgetPlacement(WidgetMode mode, int x, int y)
    {
        Mode = mode;
        X = x;
        Y = y;
    }

    public WidgetMode Mode { get; }

    public int X { get; }

    public int Y { get; }

    public WidgetPlacement WithPosition(int x, int y) => new(Mode, x, y);

    public WidgetPlacement WithMode(WidgetMode mode) => new(mode, X, Y);

    public override string ToString() => $"{WidgetPlacementStore.SerializeMode(Mode)} ({X},{Y})";
}

/// <summary>
/// 卡片放置的**持久化**（<c>settings</c> 表的三个键）与解析。
///
/// 为什么单独一层：它要能被**确定性自检**直接驱动（喂一个真实的 <see cref="SqliteStore"/>，
/// 存 → 重新打开 → 读，断言值不变），而不必先起一个完整的 <c>ProductRuntime</c>。
/// 解析也放在这里，于是“非法值该回退成什么”只有一个地方定义。
///
/// 存储不可用（降级 / <c>--no-store</c>）时全部退化为默认值，且**永不抛异常** ——
/// 一个装饰件的坐标不该让产品起不来。
/// </summary>
internal static class WidgetPlacementStore
{
    /// <summary>形态键。</summary>
    public const string KeyMode = "widget_mode";

    /// <summary>左上角 X 键。</summary>
    public const string KeyX = "widget_pos_x";

    /// <summary>左上角 Y 键。</summary>
    public const string KeyY = "widget_pos_y";

    public const string ModeEmbedded = "embedded";
    public const string ModeFloating = "floating";

    /// <summary>读放置。缺键、非法值、存储不可用 → 返回 <paramref name="fallback"/>。</summary>
    public static WidgetPlacement Load(SqliteStore? store, WidgetPlacement fallback)
    {
        if (store is null) return fallback;

        try
        {
            string? modeText = store.GetSetting(KeyMode);
            string? xText = store.GetSetting(KeyX);
            string? yText = store.GetSetting(KeyY);

            WidgetMode mode = TryParseMode(modeText, out WidgetMode parsedMode) ? parsedMode : fallback.Mode;
            int x = TryParseInt(xText, out int parsedX) ? parsedX : fallback.X;
            int y = TryParseInt(yText, out int parsedY) ? parsedY : fallback.Y;

            return new WidgetPlacement(mode, x, y);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>写坐标。存储不可用或写失败时静默忽略（坐标丢失不等于功能损坏）。</summary>
    public static void SavePosition(SqliteStore? store, int x, int y)
    {
        if (store is null) return;

        try
        {
            store.SetSetting(KeyX, x.ToString(CultureInfo.InvariantCulture));
            store.SetSetting(KeyY, y.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // 忽略：下次移动会再存一次。
        }
    }

    /// <summary>写形态。</summary>
    public static void SaveMode(SqliteStore? store, WidgetMode mode)
    {
        if (store is null) return;

        try
        {
            store.SetSetting(KeyMode, SerializeMode(mode));
        }
        catch
        {
            // 忽略：下次切换会再存一次。
        }
    }

    public static string SerializeMode(WidgetMode mode) => mode == WidgetMode.Floating ? ModeFloating : ModeEmbedded;

    /// <summary>
    /// 解析形态字符串。**大小写不敏感**；空/未知一律返回 <c>false</c>（由调用方决定回退值）。
    /// 未知值不被当作“浮动”：默认形态是嵌入，库被外部改坏时宁可按默认来，也不悄悄换个形态。
    /// </summary>
    public static bool TryParseMode(string? text, out WidgetMode mode)
    {
        mode = WidgetMode.Embedded;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (trimmed.Equals(ModeEmbedded, StringComparison.OrdinalIgnoreCase))
        {
            mode = WidgetMode.Embedded;
            return true;
        }
        if (trimmed.Equals(ModeFloating, StringComparison.OrdinalIgnoreCase))
        {
            mode = WidgetMode.Floating;
            return true;
        }
        return false;
    }

    public static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// 把坐标夹回“至少有一块在屏幕内”。
    ///
    /// 为什么需要：坐标是**上一次会话**存下来的，中间可能换过分辨率或拔过显示器，
    /// 直接照搬会让卡片落在屏幕外 —— 那就等于卡片消失了，而且**屏幕上没有任何提示**。
    /// 这里保证卡片左上角落在 <c>[0, 屏宽-1] × [0, 屏高-1]</c>（简单、可断言）。
    /// </summary>
    public static WidgetPlacement ClampToScreen(WidgetPlacement placement, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return placement;

        int x = Math.Min(Math.Max(placement.X, 0), screenWidth - 1);
        int y = Math.Min(Math.Max(placement.Y, 0), screenHeight - 1);
        return placement.WithPosition(x, y);
    }
}
