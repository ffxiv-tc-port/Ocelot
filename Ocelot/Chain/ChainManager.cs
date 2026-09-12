using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;

namespace Ocelot.Chain;

public static class ChainManager
{
    private readonly static Dictionary<string, ChainQueue> queues = [];

    public static IReadOnlyDictionary<string, ChainQueue> Queues
    {
        get => queues;
    }

    private static bool Initialized;

    public static ChainQueue Get(string id)
    {
        lock (queues)
        {
            if (!queues.TryGetValue(id, out var queue))
            {
                queue = new ChainQueue();
                queues[id] = queue;
            }

            return queue;
        }
    }

    internal static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;

        Svc.Framework.Update += Tick;
    }

    /// <remarks>
    /// 🔴 <c>ChainQueue.Dispose()</c> 現在會同步跑呼叫端的取消收尾（<c>OnCancel</c>／
    /// <c>OnFinally</c>），那是別人的碼 —— 一旦它回頭呼叫 <see cref="Get"/>，
    /// 就會在 <c>foreach</c> 走訪 <c>queues</c> 的當下新增鍵值，擲出
    /// <c>InvalidOperationException</c>。所以先在鎖內把要收的佇列從字典裡拿掉，
    /// 真正的回收放到鎖外、走訪結束之後才做。
    /// </remarks>
    private static void Tick(IFramework framework)
    {
        List<ChainQueue> toDispose = [];

        lock (queues)
        {
            var toRemove = new List<string>();

            foreach (var pair in queues)
            {
                var id = pair.Key;
                var queue = pair.Value;

                queue.Tick(framework);

                if (queue is { IsRunning: false, QueueCount: 0, TimeAlive.Seconds: >= 1 })
                {
                    if (queue.HasRun)
                    {
                        Logger.Debug($"Disposing ChainQueue '{id}' (inactive and empty)");
                    }

                    toDispose.Add(queue);
                    toRemove.Add(id);
                }
            }

            foreach (var id in toRemove)
            {
                queues.Remove(id);
            }
        }

        foreach (var queue in toDispose)
        {
            queue.Dispose();
        }
    }

    public static void AbortAll()
    {
        ChainQueue[] snapshot;
        lock (queues)
        {
            snapshot = [.. queues.Values];
        }

        // 鎖外中止：取消收尾是呼叫端的碼，可能回頭 Get() 一個新的佇列。
        foreach (var queue in snapshot)
        {
            queue.Abort();
        }

        Logger.Debug("Aborted all active ChainQueues.");
    }

    public static void Close()
    {
        Svc.Framework.Update -= Tick;

        ChainQueue[] snapshot;
        lock (queues)
        {
            snapshot = [.. queues.Values];
            queues.Clear();
            Initialized = false;
        }

        foreach (var queue in snapshot)
        {
            queue.Dispose();
        }
    }
}
