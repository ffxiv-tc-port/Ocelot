using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.Automation.NeoTaskManager;

namespace Ocelot.Chain;

public class ChainQueue : IDisposable
{
    private readonly LinkedList<Func<Chain>> chains = [];

    private Chain? chain = null;

    private DateTime createdAt { get; } = DateTime.UtcNow;

    public bool HasRun { get; private set; } = false;

    public TimeSpan TimeAlive
    {
        get => DateTime.UtcNow - createdAt;
    }

    public int ChainsCompleted { get; private set; } = 0;

    public void Submit(Func<Chain> factory)
    {
        HasRun = true;
        lock (chains)
        {
            chains.AddLast(factory);
        }
    }

    public void Submit(Func<Chain, Chain> factory)
    {
        HasRun = true;
        lock (chains)
        {
            chains.AddLast(() => factory(Chain.Create()));
        }
    }

    public void SubmitFront(Func<Chain> factory)
    {
        HasRun = true;
        lock (chains)
        {
            chains.AddFirst(factory);
        }
    }

    public void Submit(ChainFactory factory)
    {
        Submit(factory.Factory());
    }

    public void SubmitFront(ChainFactory factory)
    {
        SubmitFront(factory.Factory());
    }

    public void Submit(TaskManagerTask task)
    {
        Submit(() => Chain.Create().Then(task));
    }

    public void SubmitFront(TaskManagerTask task)
    {
        SubmitFront(() => Chain.Create().Then(task));
    }

    /// <remarks>
    /// <c>Chain.Abort()</c> 會<b>同步</b>跑呼叫端的取消收尾（<c>OnCancel</c>／<c>OnFinally</c>），那段是別人的碼，
    /// 可能回頭 <c>Submit</c> 新的動作鏈 ⇒ 先在鎖內清空佇列並取走目前這條鏈，鎖外才中止它：
    /// 收尾裡新排進來的東西不會被這一輪的 <c>Clear()</c> 掃掉。
    /// </remarks>
    public void Abort()
    {
        TakeCurrent()?.Abort();

        Logger.Debug("Aborted current chain and cleared the queue.");
    }

    /// <summary>把「目前這條鏈」取走並清空待跑佇列；回傳的鏈由呼叫端負責收掉。</summary>
    private Chain? TakeCurrent()
    {
        lock (chains)
        {
            chains.Clear();

            var current = chain;
            chain = null;
            return current;
        }
    }

    public void Clear()
    {
        lock (chains)
        {
            chains.Clear();
        }
    }

    /// <summary>把還沒開始的這一條（同一個委派實例）從待跑佇列拿掉；已經開始跑或不在佇列裡回 false。</summary>
    internal bool Remove(Func<Chain> factory)
    {
        lock (chains)
        {
            for (var node = chains.First; node != null; node = node.Next)
            {
                if (ReferenceEquals(node.Value, factory))
                {
                    chains.Remove(node);
                    return true;
                }
            }

            return false;
        }
    }

    public Chain? CurrentChain
    {
        get => chain;
    }

    public void Tick(IFramework framework)
    {
        if (chain != null && !chain.IsComplete())
        {
            return;
        }

        if (chain?.IsComplete() == true)
        {
            chain.Dispose();
            chain = null;
            ChainsCompleted++;
        }

        lock (chains)
        {
            if (chains.Count == 0)
            {
                chain = null;
                return;
            }

            var factory = chains.First!.Value;
            chains.RemoveFirst();
            chain = factory();
        }
    }

    public bool IsRunning
    {
        get => chain != null && !chain.IsComplete();
    }

    public int QueueCount
    {
        get
        {
            lock (chains)
            {
                return chains.Count;
            }
        }
    }

    /// <remarks>
    /// 📌 只呼叫 <c>Abort()</c> 就夠：<c>Chain.Abort()</c> 自己會把子動作鏈一起收掉、
    /// 跑完取消收尾之後呼叫 <c>Dispose()</c>。
    /// </remarks>
    public void Dispose()
    {
        TakeCurrent()?.Abort();
    }
}
