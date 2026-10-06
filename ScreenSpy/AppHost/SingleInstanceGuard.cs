using System;
using System.Threading;

namespace ScreenSpy.AppHost;

/// <summary>
/// 单实例保护（本次确认加入）。
///
/// **为什么必须有**：M4 的写入是**增量累加**（<c>seconds = seconds + 增量</c>），
/// 目的是让“提交成功才扣减待写数据”。代价是：两个进程同写一个库会**互相累加**，
/// 数据直接偏大且事后难以察觉。产品启动已改为打开默认库，双击两次就会踩到，
/// 因此保护不能等到 M12。
///
/// 实现：命名互斥体（<c>Local\</c> = 当前会话）。选择 <c>Local</c> 而不是 <c>Global</c>：
///  * 这是**每用户**的桌面工具，同一会话内唯一即可；
///  * <c>Global</c> 在受限账户下可能因 ACL 抛 <see cref="UnauthorizedAccessException"/>。
///
/// **失败方向的选择**：若互斥体本身创建失败（权限等），本类选择
/// **放行运行**并把原因记入 <see cref="Error"/>，而不是拒绝启动 ——
/// 因为“保护失效”不该演变成“程序打不开”，上层会把 <see cref="Error"/> 显示为警告。
/// 这也与“启动失败降级继续运行”的既定策略一致。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>默认互斥体名（会话内可见）。</summary>
    public const string DefaultName = @"Local\ScreenSpy.SingleInstance";

    private Mutex? _mutex;
    private int _disposed;

    private SingleInstanceGuard(bool isOwner, Mutex? mutex, string? error)
    {
        IsOwner = isOwner;
        _mutex = mutex;
        Error = error;
    }

    /// <summary>是否取得了所有权（true = 本进程是唯一实例，或保护不可用而被放行）。</summary>
    public bool IsOwner { get; }

    /// <summary>保护不可用的原因（正常为 null）。非 null 时 <see cref="IsOwner"/> 恒为 true。</summary>
    public string? Error { get; }

    /// <summary>尝试取得单实例所有权。</summary>
    public static SingleInstanceGuard Acquire(string name = DefaultName)
    {
        Mutex? mutex = null;
        try
        {
            // initiallyOwned: true —— 若互斥体尚不存在则由本线程持有（createdNew = true）。
            // 若已存在（说明另一个实例在运行），createdNew = false 且本线程**不会**获得所有权。
            mutex = new Mutex(initiallyOwned: true, name: name, createdNew: out bool createdNew);

            if (createdNew) return new SingleInstanceGuard(isOwner: true, mutex: mutex, error: null);

            // 已有实例：立刻释放自己的句柄（未持有所有权，Dispose 不会误释放对方的）。
            mutex.Dispose();
            return new SingleInstanceGuard(isOwner: false, mutex: null, error: null);
        }
        catch (Exception ex)
        {
            // 保护不可用：放行运行，但把原因带回上层显示为警告。
            try { mutex?.Dispose(); } catch { /* 忽略 */ }
            return new SingleInstanceGuard(isOwner: true, mutex: null, error: ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Mutex? mutex = _mutex;
        _mutex = null;
        if (mutex is null) return;

        try { mutex.ReleaseMutex(); } catch { /* 非持有者调用会抛，忽略 */ }
        try { mutex.Dispose(); } catch { /* 忽略 */ }
    }
}
