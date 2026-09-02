using System;
using Dalamud.Plugin.Services;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;

namespace Ocelot.Chain;

public class Chain : IDisposable
{
    private readonly TaskManager tasks;

    private readonly ChainContext context = new();

    public readonly string Name;

    public float Progress
    {
        get => tasks.Progress;
    }

    public int TotalLinks
    {
        get => tasks.MaxTasks;
    }

    public int CompletedLinks
    {
        get => TotalLinks - tasks.NumQueuedTasks;
    }

    private event Action? OnCancelCallback;

    private event Action? OnCompleteCallback;

    private event Action? OnFinallyCallback;

    private bool hasTriggeredClosingTasks;

    private DateTime createdAt { get; } = DateTime.UtcNow;

    public TimeSpan TimeAlive
    {
        get => DateTime.UtcNow - createdAt;
    }

    private Chain(string name, TaskManagerConfiguration? defaultConfiguration = null)
    {
        Name = name;

        if (defaultConfiguration == null)
        {
            defaultConfiguration = new TaskManagerConfiguration
            {
                TimeLimitMS = int.MaxValue,
            };
        }

        tasks = new TaskManager(defaultConfiguration);

        Svc.Framework.Update += Tick;

        Debug($"Starting Chain [{Name}]");
    }

    public static Chain Create(string name, TaskManagerConfiguration? defaultConfiguration = null)
    {
        return new Chain(name, defaultConfiguration);
    }

    public static Chain Create(TaskManagerConfiguration? defaultConfiguration = null)
    {
        return Create("Unnamed", defaultConfiguration);
    }

    private void Tick(IFramework _)
    {
        if (context.token.IsCancellationRequested && !IsMainComplete() && !hasTriggeredClosingTasks)
        {
            Logger.Debug($"Chain [{Name}] was cancelled.");
            tasks.Abort();

            hasTriggeredClosingTasks = true;
            tasks.Enqueue(() => OnCancelCallback?.Invoke());
            tasks.Enqueue(() => OnFinallyCallback?.Invoke());
        }
        else if (IsMainComplete() && !hasTriggeredClosingTasks)
        {
            hasTriggeredClosingTasks = true;
            tasks.Enqueue(() => OnCompleteCallback?.Invoke());
            tasks.Enqueue(() => OnFinallyCallback?.Invoke());
        }
    }

    /// <summary>
    /// 排入一個「執行一次就算完成」的步驟。
    /// </summary>
    /// <remarks>
    /// 注意:本多載會吞掉步驟的回傳值。ECommons NeoTaskManager 的
    /// <c>TaskManagerTask(Action)</c> 建構子把委派包成 <c>{ action(); return true; }</c>
    /// (TaskManagerTask.cs:29 與 :57),因此步驟裡回傳的 <c>false</c> 不會讓它重跑,
    /// <c>TimeLimitMS</c> 也完全不參與 —— 步驟被守衛擋下時的結果是
    /// 「這一輪永久跳過」,不是「下一輪再來」。
    /// <para>
    /// 綁到哪一個 Then 多載,由 lambda 主體「回不回值」決定,不是由「有沒有寫成 lambda」決定
    /// (2026-09-03 以編譯器實測):
    /// <list type="bullet">
    /// <item><c>ctx =&gt; 回 void 的方法(ctx)</c> 綁本多載(做一次)</item>
    /// <item><c>ctx =&gt; 回 bool 的方法(ctx)</c> 綁 <c>Then(Func&lt;ChainContext, bool?&gt;)</c>,會重試到 true</item>
    /// <item><c>ctx =&gt; { ...; return b; }</c> 同樣綁 <c>Then(Func&lt;ChainContext, bool?&gt;)</c></item>
    /// <item>方法群組寫法 <c>Then(回 bool 的方法)</c> 會編譯失敗 CS0407;
    /// 用捨棄回傳值繞過(<c>ctx =&gt; { _ = 方法(ctx); }</c>)會靜默掉回本多載</item>
    /// </list>
    /// </para>
    /// <para>
    /// 需要「被擋下就重試」的步驟請改用 <c>Then(Func&lt;ChainContext, bool?&gt;)</c>;
    /// 要有界重試請照 <c>Ocelot.Chain.ChainEx.ChainAddon.AddonCallback</c> 的寫法自己建
    /// <c>TaskManagerTask</c> 加 <c>TaskManagerConfiguration</c>,再用 <c>Then(TaskManagerTask)</c>。
    /// </para>
    /// </remarks>
    public Chain Then(Action<ChainContext> action)
    {
        tasks.Enqueue(() => action(context));
        return this;
    }

    /// <summary>
    /// 條件成立時插入一個「執行一次就算完成」的步驟。
    /// </summary>
    /// <remarks>
    /// 兩個容易踩的地方:
    /// <list type="number">
    /// <item>與 <c>Then(Action&lt;ChainContext&gt;)</c> 一樣會吞掉回傳值,而且 ConditionalThen
    /// 沒有 <c>Func&lt;ChainContext, bool?&gt;</c> 多載 —— 回 bool 的 lambda 會靜默綁到本多載
    /// 並被吞掉(2026-09-03 編譯器實測)。需要重試請改用
    /// <c>ConditionalThen(condition, TaskManagerTask)</c>。</item>
    /// <item>如果 lambda 主體是「建一條子 Chain」,回傳的 <c>Chain</c> 會被丟掉,
    /// 子 Chain 變成脫鉤執行:外層不會等它,也沒有人 Dispose 它。
    /// 要讓外層等待,請把它寫成零參數 lambda(<c>() =&gt; Chain.Create()...</c>),
    /// 那才會綁到 <c>ConditionalThen(condition, Func&lt;Chain&gt;, TaskManagerConfiguration?)</c>。</item>
    /// </list>
    /// </remarks>
    public Chain ConditionalThen(Func<ChainContext, bool> condition, Action<ChainContext> action)
    {
        tasks.Enqueue(() =>
        {
            if (condition(context))
            {
                tasks.Insert(() => action(context));
            }
        });

        return this;
    }

    public Chain Then(TaskManagerTask task)
    {
        tasks.EnqueueMulti(task);
        return this;
    }

    /// <summary>
    /// 排入一個會「重複執行到回傳 true」的步驟;回傳 <c>null</c> 代表中止整條佇列。
    /// </summary>
    /// <remarks>
    /// 這是需要重試或等待條件時的正確入口(回 bool 的 lambda 會自動綁到這裡)。
    /// 注意:本多載沒有 <c>TaskManagerConfiguration</c> 參數,會沿用 TaskManager 的預設值,
    /// 而本類別的預設值是 <c>TimeLimitMS = int.MaxValue</c>,無上限重試;
    /// 2026-09-03 全庫清點:BOCCHI 與 Ocelot 沒有任何一處 <c>Chain.Create</c> 傳入自訂設定,
    /// 所以一直回傳 <c>false</c> 的步驟會把整條 Chain 永遠卡住,不逾時、也不會留下 log。
    /// 要有界重試請自己建
    /// <c>new TaskManagerTask(func, new TaskManagerConfiguration { TimeLimitMS = ... })</c>
    /// 再用 <c>Then(TaskManagerTask)</c>。
    /// </remarks>
    public Chain Then(Func<ChainContext, bool?> factory)
    {
        tasks.EnqueueMulti(new TaskManagerTask(() => factory(context)));
        return this;
    }

    public Chain ConditionalThen(Func<ChainContext, bool> condition, TaskManagerTask task)
    {
        tasks.Enqueue(() =>
        {
            if (condition(context))
            {
                tasks.InsertMulti(task);
            }
        });

        return this;
    }

    public Chain Then(Func<Chain> factory, TaskManagerConfiguration? config = null)
    {
        Chain? chain = null;
        return Then(new TaskManagerTask(() =>
        {
            if (chain == null)
            {
                chain = factory();
                Logger.Debug($"Creating chain {chain.Name} from factory");
            }

            return chain.IsComplete();
        }, config));
    }

    public Chain ConditionalThen(Func<ChainContext, bool> condition, Func<Chain> factory, TaskManagerConfiguration? config = null)
    {
        tasks.Enqueue(() =>
        {
            if (condition(context))
            {
                var chain = factory();
                tasks.InsertMulti(new TaskManagerTask(() => chain.IsComplete(), config));
            }
        });

        return this;
    }

    public Chain Then(ChainFactory chain)
    {
        return Then(chain.Factory(), chain.Config());
    }

    public Chain ConditionalThen(Func<ChainContext, bool> condition, ChainFactory chain)
    {
        return ConditionalThen(condition, chain.Factory(), chain.Config());
    }

    public Chain Wait(int delay)
    {
        tasks.EnqueueDelay(delay);
        return this;
    }

    public Chain ConditionalWait(Func<ChainContext, bool> condition, int delay)
    {
        tasks.Enqueue(() =>
        {
            if (condition(context))
            {
                tasks.InsertDelay(delay);
            }
        });

        return this;
    }

    public Chain SubChain(string name, Func<Chain, Chain> subChainFactory, TaskManagerConfiguration? config = null)
    {
        return Then(() => subChainFactory(Create(name)), config);
    }

    public Chain SubChain(Func<Chain, Chain> subChainFactory, TaskManagerConfiguration? config = null)
    {
        return Then(() => subChainFactory(Create()), config);
    }

    public Chain Info(string message)
    {
        return Then(_ => Logger.Info(message));
    }

    public Chain Log(string message)
    {
        return Info(message);
    }

    public Chain Error(string message)
    {
        return Then(_ => Logger.Error(message));
    }

    public Chain Debug(string message)
    {
        return Then(_ => Logger.Debug(message));
    }

    public void Abort()
    {
        Svc.Log.Info($"Aborting chain [{Name}]");
        tasks.Abort();

        Dispose();
    }

    public bool IsMainComplete()
    {
        return tasks is { IsBusy: false, NumQueuedTasks: 0 };
    }

    public bool IsComplete()
    {
        return IsMainComplete() && hasTriggeredClosingTasks;
    }

    public Chain OnCancel(Action callback)
    {
        OnCancelCallback += callback;
        return this;
    }

    public Chain OnComplete(Action callback)
    {
        OnCompleteCallback += callback;
        return this;
    }

    public Chain OnFinally(Action callback)
    {
        OnFinallyCallback += callback;
        return this;
    }

    public void Dispose()
    {
        Svc.Log.Info($"Disposing chain [{Name}]");
        tasks.Dispose();

        OnCancelCallback = null;
        OnCompleteCallback = null;
        OnFinallyCallback = null;

        Svc.Framework.Update -= Tick;
    }
}
