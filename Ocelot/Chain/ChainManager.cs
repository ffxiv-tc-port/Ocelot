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
    /// <c>ChainQueue.Dispose()</c> 會同步跑呼叫端的取消收尾（<c>OnCancel</c>／
    /// <c>OnFinally</c>），那是別人的碼 —— 一旦它回頭呼叫 <see cref="Get"/>，
    /// 就會在 <c>foreach</c> 走訪 <c>queues</c> 的當下新增鍵值，擲出
    /// <c>InvalidOperationException</c>。所以先在鎖內把要收的佇列從字典裡拿掉，
    /// 真正的回收放到鎖外、走訪結束之後才做。
    /// </remarks>
    private static void Tick(IFramework framework)
    {
        // 兩份清單都等真的有佇列要收才建：絕大多數的幀沒有。
        List<ChainQueue>? toDispose = null;

        lock (queues)
        {
            List<string>? toRemove = null;

            foreach (var pair in queues)
            {
                var id = pair.Key;
                var queue = pair.Value;

                queue.Tick(framework);

                // 用總秒數：TimeSpan.Seconds 只是 0～59 的秒數部分，存活剛滿整分鐘的空佇列會被判成「還不到 1 秒」。
                if (queue is { IsRunning: false, QueueCount: 0, TimeAlive.TotalSeconds: >= 1.0 })
                {
                    if (queue.HasRun)
                    {
                        Logger.Debug($"Disposing ChainQueue '{id}' (inactive and empty)");
                    }

                    (toDispose ??= []).Add(queue);
                    (toRemove ??= []).Add(id);
                }
            }

            if (toRemove != null)
            {
                foreach (var id in toRemove)
                {
                    queues.Remove(id);
                }
            }
        }

        if (toDispose != null)
        {
            foreach (var queue in toDispose)
            {
                queue.Dispose();
            }
        }
    }

    /// <summary>任何執行緒都可以呼叫：鎖內拷一份目前的佇列表。</summary>
    internal static KeyValuePair<string, ChainQueue>[] SnapshotQueues()
    {
        lock (queues)
        {
            return [.. queues];
        }
    }

    /// <summary>只在 framework 執行緒呼叫：會同步中止動作鏈（ECommons <c>TaskManager</c> 不是執行緒安全的）。</summary>
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
