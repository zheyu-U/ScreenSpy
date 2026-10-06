using System;
using System.Runtime.InteropServices;
using ScreenSpy.Interop;

namespace ScreenSpy.Collector;

/// <summary>
/// Windows 实现：用 <c>WTSQuerySessionInformation(WTSSessionInfoEx)</c> 探测锁屏状态。
///
/// 为什么不用 <c>OpenInputDesktop</c>：它在 UAC 安全桌面（以及某些会话配置）下同样返回失败，
/// 会把“提权对话框”误判成锁屏；而 WTS 的 <c>SessionFlags</c> 只反映真实的锁定状态。
///
/// **关于结构体对齐（本实现的核心难点）**：<c>WTSINFOEX</c> 是
/// <c>{ DWORD Level; union { WTSINFOEX_LEVEL1; WTSINFOEX_LEVEL2; } }</c>，
/// 两个 union 成员内部含 <c>LARGE_INTEGER</c>（8 字节对齐），因此 <c>SessionId</c>
/// **不一定**紧跟在 <c>Level</c> 之后。本机（26H2 / 26300.9457）实测：
/// <c>Level</c> 在偏移 0，**有 4 字节填充**，<c>SessionId</c> 在偏移 8，<c>SessionState</c> 在 12，
/// <c>SessionFlags</c> 在 16（见 <c>--m2-selfcheck</c> 打印的“自定位偏移=8”）。
///
/// 手写固定偏移会在这种场合静默读到错位数据，所以这里改为**自定位 + 交叉校验**：
///  1. 用 <c>WTSSessionId</c> 查得真实会话号；
///  2. 用 <c>WTSConnectState</c> 查得真实连接状态（独立来源，用于交叉校验）；
///  3. 在 <c>SessionInfoEx</c> 缓冲区里寻找“等于会话号的 DWORD”，并要求其后两个 DWORD
///     分别等于**真实连接状态**与合法的 <c>SessionFlags</c>（0/1）。
///
/// **一个已排除的坑（值得留档）**：扫描必须从偏移 **4** 起步。
/// 因为 <c>Level</c> 恰好等于 1，当会话号也是 1（控制台会话很常见）时，
/// 从偏移 0 扫描会命中错位的“伪头部”，而其后两个 DWORD（填充 0 + 会话号 1）
/// 恰好又是合法值 —— 于是每台会话号为 1 的机器都会被**误判**。
/// 要求“候选偏移 ≥ 4”（<c>SessionId</c> 必然在 <c>Level</c> 之后）即可根治；
/// 这一场景已写成确定性回归检查（见 M2SelfCheck）。
/// </summary>
internal sealed class Win32SessionStateProbe : ISessionStateProbe
{
    /// <summary>WTS_CONNECTSTATE_CLASS.WTSActive —— 只有活动会话的 SessionFlags 才有意义。</summary>
    private const int WtsActive = 0;

    /// <summary>WTSINFOEX.Level == 1 表示后面是 WTSINFOEX_LEVEL1。</summary>
    private const int WtsInfoExLevel1 = 1;

    /// <summary>WTS_SESSIONSTATE_LOCK</summary>
    private const int WtsSessionStateLock = 0;

    /// <summary>WTS_SESSIONSTATE_UNLOCK</summary>
    private const int WtsSessionStateUnlock = 1;

    /// <summary><c>SessionId</c> 必然位于 <c>Level</c>（4 字节）之后，故扫描起点为 4。</summary>
    private const int ScanStart = 4;

    /// <summary>
    /// 扫描窗口上限：头部三个 DWORD 且 <c>Level</c> 之后最多 4 字节填充，落在 24 字节内绰绰有余。
    /// 窗口越窄，命中巧合值的概率越低。
    /// </summary>
    private const int ScanLimit = 24;

    /// <summary>最近一次探测的可读诊断（含自定位到的偏移，便于取证）。</summary>
    public string LastDiagnostic { get; private set; } = "(尚未探测)";

    /// <summary>最近一次自定位命中的缓冲区偏移（-1 表示未命中）。</summary>
    public int LastSessionIdOffset { get; private set; } = -1;

    public SessionLockProbeResult ProbeLocked()
    {
        try
        {
            return ProbeCore();
        }
        catch (Exception ex)
        {
            LastDiagnostic = "探测异常：" + ex.GetType().Name + ": " + ex.Message;
            return SessionLockProbeResult.Unknown;
        }
    }

    private SessionLockProbeResult ProbeCore()
    {
        byte[]? idBytes = QueryRaw(NativeMethods.WTS_INFO_SESSION_ID);
        if (idBytes is null || idBytes.Length < 4)
        {
            // 多见于：服务会话 / 无控制台会话。
            LastDiagnostic = "WTSSessionId 查询失败或被截断。";
            return SessionLockProbeResult.Unknown;
        }

        uint sessionId = BitConverter.ToUInt32(idBytes, 0);

        // 独立来源的连接状态（交叉校验用）。取不到就退化为“不做该项校验”。
        byte[]? stateBytes = QueryRaw(NativeMethods.WTS_INFO_CONNECT_STATE);
        int? expectedState = stateBytes is { Length: >= 4 }
            ? BitConverter.ToInt32(stateBytes, 0)
            : null;

        byte[]? exBytes = QueryRaw(NativeMethods.WTS_INFO_SESSION_INFO_EX);
        if (exBytes is null || exBytes.Length < 16)
        {
            LastDiagnostic = $"WTSSessionInfoEx 查询失败或被截断（会话 {sessionId}）。";
            return SessionLockProbeResult.Unknown;
        }

        int level = BitConverter.ToInt32(exBytes, 0);
        if (level != WtsInfoExLevel1)
        {
            LastDiagnostic = $"WTSSessionInfoEx 级别为 {level}，期望 {WtsInfoExLevel1}。";
            return SessionLockProbeResult.Unknown;
        }

        if (!TryLocateHeader(exBytes, sessionId, expectedState, out int offset, out int state, out int flags))
        {
            LastSessionIdOffset = -1;
            LastDiagnostic =
                $"未能自定位会话 {sessionId} 的头部（缓冲区 {exBytes.Length} 字节，" +
                $"期望连接状态 {Describe(expectedState)}）。";
            return SessionLockProbeResult.Unknown;
        }

        LastSessionIdOffset = offset;
        SessionLockProbeResult result = Classify(state, flags);

        LastDiagnostic = result switch
        {
            SessionLockProbeResult.Locked =>
                $"会话 {sessionId} 活动，SessionFlags={flags}（LOCK），自定位偏移={offset}。",
            SessionLockProbeResult.Unlocked =>
                $"会话 {sessionId} 活动，SessionFlags={flags}（UNLOCK），自定位偏移={offset}。",
            _ =>
                $"会话 {sessionId} 非活动态（SessionState={state}），SessionFlags 无定义 → 不作判定。" +
                $"自定位偏移={offset}。",
        };

        return result;
    }

    /// <summary>
    /// 把“会话连接状态 + SessionFlags”翻译成结论（纯函数，便于确定性检查）。
    ///
    /// 关键：只有**活动会话**的 <c>SessionFlags</c> 才有定义。
    /// 非活动态（快速用户切换 / RDP 接管控制台等）必须返回 Unknown，
    /// 绝不能拿一个无定义的标志位去覆盖事件给出的状态。
    /// </summary>
    internal static SessionLockProbeResult Classify(int sessionState, int sessionFlags)
    {
        if (sessionState != WtsActive) return SessionLockProbeResult.Unknown;
        if (sessionFlags == WtsSessionStateLock) return SessionLockProbeResult.Locked;
        if (sessionFlags == WtsSessionStateUnlock) return SessionLockProbeResult.Unlocked;
        return SessionLockProbeResult.Unknown;
    }

    /// <summary>
    /// 在 <c>WTSINFOEX</c> 缓冲区中自定位会话头部（纯函数，便于确定性检查）。
    ///
    /// 命中条件（三者必须同时成立）：
    ///  * 该 DWORD 等于真实会话号；
    ///  * 紧随其后的 DWORD 等于**真实连接状态**（来自独立的 WTSConnectState 查询，可能缺省）；
    ///  * 再下一个 DWORD 是合法的 SessionFlags（0 = 锁定，1 = 未锁定）。
    /// 且候选偏移必须 ≥ <see cref="ScanStart"/>（见类注释里的“已排除的坑”）。
    /// </summary>
    internal static bool TryLocateHeader(byte[] exBytes, uint sessionId, int? expectedState,
                                         out int offset, out int sessionState, out int sessionFlags)
    {
        offset = -1;
        sessionState = 0;
        sessionFlags = 0;

        if (exBytes is null || exBytes.Length < ScanStart + 12) return false;

        int limit = Math.Min(ScanLimit, exBytes.Length - 12);

        for (int off = ScanStart; off <= limit; off += 4)
        {
            if (BitConverter.ToUInt32(exBytes, off) != sessionId) continue;

            int state = BitConverter.ToInt32(exBytes, off + 4);
            int flags = BitConverter.ToInt32(exBytes, off + 8);

            if (expectedState is { } expected && state != expected) continue;
            if (state is < 0 or > 9) continue;
            if (flags is not (WtsSessionStateLock or WtsSessionStateUnlock)) continue;

            offset = off;
            sessionState = state;
            sessionFlags = flags;
            return true;
        }

        return false;
    }

    private static string Describe(int? state) => state is { } s ? s.ToString() : "(未知)";

    /// <summary>
    /// 调用 <c>WTSQuerySessionInformation</c> 并把非托管缓冲区复制成托管数组。
    /// 无论成败都会释放 WTS 分配的内存（必须用 <c>WTSFreeMemory</c>，不能用 FreeHGlobal）。
    /// </summary>
    private static byte[]? QueryRaw(uint wtsInfoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        uint bytes = 0;

        try
        {
            bool ok = NativeMethods.WTSQuerySessionInformation(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                NativeMethods.WTS_CURRENT_SESSION,
                wtsInfoClass,
                out buffer,
                out bytes);

            if (!ok || buffer == IntPtr.Zero || bytes == 0) return null;

            var data = new byte[bytes];
            Marshal.Copy(buffer, data, 0, (int)bytes);
            return data;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NativeMethods.WTSFreeMemory(buffer);
        }
    }
}
