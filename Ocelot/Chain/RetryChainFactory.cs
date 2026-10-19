using System;
using ECommons.Automation.NeoTaskManager;
using ECommons.Throttlers;

namespace Ocelot.Chain;

public abstract class RetryChainFactory : ChainFactory
{
    protected int attempt = 0;

    /// <remarks>
    /// <para>外殼一直活到它送出的嘗試跑完：<see cref="GetTimeout"/> 只算「沒有嘗試在 ##watcher 佇列裡」的累計時間，
    /// 一次嘗試本身可以比它長（返回＝詠唱＋讀圖＋增益＋走向魔路）。外殼先收場，呼叫端就會在嘗試還在跑時往下走。</para>
    /// <para>預算用完時由最後一步逾時收場，走與其他逾時相同的取消路徑。</para>
    /// <para>外殼被中止或沒跑完就被收掉時，只停自己送出的那一次嘗試：##watcher 佇列以型別名共用，
    /// 同型別的另一個外殼送的嘗試不能動。外殼自己判定完成（<see cref="IsComplete"/>）之後，還沒跑完的嘗試照舊跑完。</para>
    /// </remarks>
    public override Func<Chain> Factory()
    {
        var name = GetType().Name;

        return () =>
        {
            var idleMs = 0L;
            var lastPollAt = 0L;
            var exhausted = false;
            var settled = false;
            var ownNumber = 0;
            ChainQueue? ownQueue = null;
            Func<Chain>? pending = null;
            Chain? running = null;

            // quietly：在 Dispose 裡不能當場跑別人的收尾（可能正在 ChainManager.Tick 走訪佇列），改由嘗試自己下一幀收尾。
            void StopOwnAttempt(bool quietly)
            {
                if (pending != null)
                {
                    var removed = ownQueue?.Remove(pending) == true;
                    pending = null;
                    if (removed)
                    {
                        Logger.Info($"動作鏈 [{name}] 被中止：第 {ownNumber} 次嘗試還沒開始，已從佇列拿掉。");
                    }

                    return;
                }

                if (running == null || running.IsComplete())
                {
                    return;
                }

                var current = running;
                running = null;
                if (quietly)
                {
                    Logger.Info($"動作鏈 [{name}] 沒跑完就被收掉：第 {ownNumber} 次嘗試還在跑，清空它的步驟，下一幀收尾。");
                    current.AbortQuietly();
                    return;
                }

                Logger.Info($"動作鏈 [{name}] 被中止：一併中止還在跑的第 {ownNumber} 次嘗試。");
                current.Abort();
            }

            return Chain.Create(name)
                .Then(new TaskManagerTask(() =>
                {
                    if (IsComplete())
                    {
                        settled = true;
                        return true;
                    }

                    var now = Environment.TickCount64;
                    var watcher = ChainManager.Get($"{name}##watcher");
                    if (watcher.IsRunning || watcher.QueueCount > 0)
                    {
                        lastPollAt = now;
                        return false;
                    }

                    if (attempt >= GetMaxAttempts())
                    {
                        Logger.Debug($"{name}: Max retry attempts reached.");
                        settled = true;
                        return true;
                    }

                    if (lastPollAt != 0)
                    {
                        idleMs += now - lastPollAt;
                    }

                    lastPollAt = now;
                    if (idleMs > GetTimeout())
                    {
                        exhausted = true;
                        return true;
                    }

                    if (EzThrottler.Throttle(name, GetThrottle()))
                    {
                        attempt++;
                        Logger.Debug($"{name}: Attempt {attempt}/{GetMaxAttempts()}");
                        ownNumber = attempt;
                        ownQueue = watcher;
                        Func<Chain> factory = () =>
                        {
                            pending = null;
                            running = Create(Chain.Create(name));
                            return running;
                        };
                        pending = factory;
                        watcher.Submit(factory);
                    }

                    return false;
                }, new TaskManagerConfiguration { TimeLimitMS = int.MaxValue }))
                .Then(new TaskManagerTask(() => !exhausted, $"{name}.RetryBudget", new TaskManagerConfiguration
                {
                    TimeLimitMS = 0,
                    OnTaskTimeout = (TaskManagerTask _, ref long _) => Logger.Warning(
                        $"動作鏈 [{name}] 重試 {attempt} 次仍未完成：嘗試之間累計等待超過 {GetTimeout()} ms，整條動作鏈會被中止，收尾走取消路徑。"),
                }))
                .OnCancel(() => StopOwnAttempt(false))
                .OnDispose(() =>
                {
                    if (!settled)
                    {
                        StopOwnAttempt(true);
                    }
                });
        };
    }

    public abstract bool IsComplete();

    public virtual int GetThrottle()
    {
        return 500;
    }

    /// <summary>嘗試之間（沒有嘗試在跑時）累計可以等多久，單位毫秒；不含嘗試本身跑的時間。</summary>
    public virtual int GetTimeout()
    {
        return 10000;
    }

    public virtual int GetMaxAttempts()
    {
        return int.MaxValue;
    }

    public override TaskManagerConfiguration? Config()
    {
        return new TaskManagerConfiguration
        {
            TimeLimitMS = int.MaxValue,
        };
    }
}
