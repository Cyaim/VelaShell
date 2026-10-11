// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格:velashell-docs/zh/ssh/spec/01-transport-framing.md §4「释放」

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 传输一端（读端或写端）的在途调用计数，以及「由谁来收尾」的交接。
/// </summary>
/// <remarks>
/// <para>
/// 那一端的缓冲来自共享内存池，收尾时还回去。而交给底层流的一次读写手里还攥着那段内存：
/// 释放时当场收尾，那次读写稍后照样往里写、从里读 —— 写进的是别人刚租走的数组。
/// 所以<b>最后离开的那一方收尾</b>：释放时没有调用在途就当场收尾；有就交给在途的调用，它退出时收尾。
/// </para>
/// <para>
/// 收尾恰好执行一次。释放之后再进来的调用一律 <see cref="ObjectDisposedException"/>。
/// </para>
/// </remarks>
internal sealed class TransportSideLifetime(object owner, Action close)
{
    private readonly Lock _gate = new();
    private int _inFlight;
    private bool _closing;
    private bool _closed;

    /// <summary>一次调用开始用这一端。已经在释放了就抛。</summary>
    public void Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, owner);
            _inFlight++;
        }
    }

    /// <summary>那次调用不再碰这一端。它是释放之后最后一个离开的，就由它收尾。</summary>
    public void Exit()
    {
        bool closeNow;
        lock (_gate)
        {
            _inFlight--;
            closeNow = _closing && _inFlight == 0 && !_closed;
            _closed |= closeNow;
        }
        if (closeNow)
        {
            close();
        }
    }

    /// <summary>释放这一端。</summary>
    /// <returns>
    /// <see langword="true"/>：没有调用在途，已经当场收尾（或早就收过）；
    /// <see langword="false"/>：还有调用在途，收尾交给它们中最后退出的那一个。
    /// </returns>
    public bool Close()
    {
        lock (_gate)
        {
            _closing = true;
            if (_closed)
            {
                return true;
            }
            if (_inFlight > 0)
            {
                return false;
            }
            _closed = true;
        }
        close();
        return true;
    }
}
