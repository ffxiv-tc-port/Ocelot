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
    /// <para>
    /// 🔴 <c>Chain.Abort()</c> 會<b>同步</b>跑呼叫端的取消收尾（<c>OnCancel</c>／<c>OnFinally</c>），
    /// 所以一定要放在 <c>lock (chains)</c> <b>外面</b>：那段是別人的碼，可能回頭
    /// <c>Submit</c> 新的動作鏈。放在鎖裡雖然不會死鎖（<c>Monitor</c> 對同一執行緒可重入），
    /// 但先 <c>Clear()</c> 再跑收尾、收尾又 Submit 的話，新排進來的東西會被這一輪的
    /// <c>Clear()</c> 掃掉 —— 失敗形式是「送出去的工作靜默消失」。
    /// ⇒ 先把佇列與目前這條鏈一起取走，鎖外才動它。
    /// </para>
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
    /// 跑完取消收尾之後呼叫 <c>Dispose()</c>。原本這裡是 <c>Abort()</c> 再 <c>Dispose()</c>，
    /// 那是同一件事做兩遍。
    /// </remarks>
    public void Dispose()
    {
        TakeCurrent()?.Abort();
    }
}
