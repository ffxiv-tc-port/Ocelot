using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;

namespace Ocelot.Chain;

public class Chain : IDisposable
{
    private readonly TaskManager tasks;

    private readonly ChainContext context = new();

    /// <summary>
    /// 這條動作鏈自己生出來、而且還活著的子動作鏈。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 子鏈的參考只活在包裝任務的閉包裡，而每條 <see cref="Chain"/> 建構時掛兩個 <c>Framework.Update</c>
    /// （自己的 <see cref="Tick"/> ＋ 它那顆 <c>TaskManager</c> 的）：不登記在這裡就沒有人收掉它，訂閱一路留到插件卸載。
    /// </para>
    /// <para>
    /// 📌 登記在這裡的子鏈有三條回收路徑：正常跑完時由包裝任務 <see cref="ReleaseChild"/>
    /// 當場收掉；外層決定收尾（逾時、例外、取消）或被 <see cref="Abort"/> 時當場中止（跑它的取消收尾）；
    /// 外層直接被 <see cref="Dispose"/> 時由它自己下一次 Tick 走取消收尾、跑完自己收掉。
    /// </para>
    /// <para>
    /// ⚠️ 一律在 <c>lock</c> 內存取：加入發生在 framework 執行緒（任務 lambda 裡），
    /// 而 <see cref="Abort"/> 可能從 UI／指令路徑進來。
    /// </para>
    /// </remarks>
    private readonly List<Chain> children = [];

    /// <summary>這條動作鏈有沒有被收掉了。<see cref="Dispose"/> 可能從多條路徑進來，要冪等。</summary>
    private bool disposed;

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

    private event Action? OnDisposeCallback;

    private bool hasTriggeredClosingTasks;

    // 收尾的種類與兩段（OnCancel／OnComplete、OnFinally）各自跑過沒：排進佇列的收尾還沒跑就被
    // Abort 清掉時，由 Abort 當場補跑沒跑過的那幾段，每段最多一次。
    private bool closingCancelled;

    private bool closingMainRan;

    private bool closingFinallyRan;

    // 外層被收掉時還活著的子鏈：外層不會再回收它，自己的取消收尾跑完就自己收掉。
    private bool disposeWhenClosed;

    /// <summary>
    /// 有沒有哪一步逾時，而且那次逾時的設定是「連整條動作鏈一起中止」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 逾時中止走 ECommons <c>TaskManager.Abort()</c>（清空佇列），之後 <see cref="IsMainComplete"/> 與「所有步驟都跑完了」
    /// 分不出來 ⇒ 只能由本鏈自己記（<see cref="SettlePendingTimeout"/>）；不記就會把逾時回報成成功（觸發 <c>OnComplete</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 只有「這次逾時真的會中止整條鏈」才記。<c>AbortOnTimeout = false</c> 的任務
    /// 逾時只丟棄自己那一步、其餘步驟照跑，那不是失敗收尾。
    /// </para>
    /// </remarks>
    private bool timedOut;

    // 逾時事件觸發過、設定是中止整條鏈，但還沒確定 TaskManager 真的中止（見 SettlePendingTimeout）。
    private TaskManagerTask? pendingTimeout;

    /// <summary>
    /// 這條動作鏈是不是以「非正常」的方式收場的 —— 逾時、步驟擲出例外、
    /// 或步驟回傳 <c>null</c>（要求中止整條佇列）；外層被收掉時的延後中止（<see cref="AbortDetached"/>／<see cref="AbortQuietly"/>）也記在這裡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 前三條路徑共用一個旗標：ECommons <c>TaskManager.Tick</c> 對它們都是呼叫 <c>Abort()</c>（清空佇列），
    /// 事後看 <see cref="IsMainComplete"/> 與「所有步驟都跑完了」分不出來，
    /// 所以只能在中止發生的當下由本鏈自己記，否則失敗會被回報成成功
    /// （<c>OnComplete</c> 被觸發，呼叫端的善後永遠不會跑）。
    /// </para>
    /// <para>
    /// 📌 收尾動作也刻意相同（<c>OnCancel</c> ＋ <c>OnFinally</c>）——
    /// 呼叫端掛在 <c>OnCancel</c> 上的善後（停下尋路、把狀態切回可用）
    /// 在這三種情況下要做的事情本來就是同一件，所以不需要三個獨立旗標。
    /// 真的要分辨「是不是逾時」請讀 <see cref="IsTimedOut"/>。
    /// </para>
    /// </remarks>
    private bool abortedAbnormally;

    /// <summary>
    /// 這條動作鏈是不是因為逾時而被中止的。
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="IsComplete"/> 在逾時的情況下<b>仍然是 <c>true</c></b>，
    /// 而且那是刻意的 —— 包著子動作鏈的外層任務（<c>Then(Func&lt;Chain&gt;)</c>）
    /// 以及 <c>ChainQueue.Tick</c> 都用它判斷「這條鏈跑完了沒」，
    /// 讓它在逾時後永遠回 <c>false</c> 會把外層卡死
    /// （<c>RetryChainFactory.Config()</c> 的外殼是 <c>TimeLimitMS = int.MaxValue</c>，
    /// 真的會永遠等下去）。要分辨「跑完」與「逾時收場」請讀這個屬性。
    /// <para>
    /// ⚠️ 這個屬性<b>只</b>回答「是不是逾時」。步驟擲出例外或回傳 <c>null</c>
    /// 同樣會走取消收尾，但那兩種情況它是 <c>false</c>
    /// —— 要問的是「有沒有正常跑完」請看 <see cref="abortedAbnormally"/> 的說明。
    /// </para>
    /// </remarks>
    public bool IsTimedOut
    {
        get => timedOut;
    }

    private DateTime createdAt { get; } = DateTime.UtcNow;

    public TimeSpan TimeAlive
    {
        get => DateTime.UtcNow - createdAt;
    }

    /// <summary>
    /// 沒有自帶設定的步驟（<c>Then(Func&lt;ChainContext, bool?&gt;)</c>，
    /// 以及設定傳 <c>null</c> 的子動作鏈）能重試多久的上限，單位毫秒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼要有上限：一直回傳 <c>false</c> 的步驟會把整條動作鏈永久卡住，而且 log 上一個字都不留。
    /// 取 10 分鐘：沒自帶設定的子動作鏈可能把好幾個有上限的等待包在裡面，寧可寬也不要誤殺。
    /// </para>
    /// <para>
    /// ⚠️ 真正需要無上限的等待要<b>明確</b>寫 <c>TimeLimitMS = int.MaxValue</c>
    /// （例：<c>RetryChainFactory.Config()</c> 的重試外殼）；自帶設定的步驟不受本預設值影響。
    /// </para>
    /// <para>
    /// ⚠️ 一次性步驟（<c>Then(Action&lt;ChainContext&gt;)</c>）第一次執行就回 <c>true</c>，
    /// 而 <c>TaskManager.Tick</c> 是在呼叫委派<b>之前</b>才檢查逾時，所以它們碰不到這個上限；
    /// <c>Wait(delay)</c> 走 ECommons 的 <c>DelayTask</c>，它自己帶
    /// <c>timeLimitMS: ms * 2 + 5000</c> 的設定，同樣不受影響。
    /// </para>
    /// </remarks>
    public const int DefaultTimeLimitMS = 600000;

    private Chain(string name, TaskManagerConfiguration? defaultConfiguration = null)
    {
        Name = name;

        if (defaultConfiguration == null)
        {
            defaultConfiguration = new TaskManagerConfiguration
            {
                TimeLimitMS = DefaultTimeLimitMS,
            };
        }

        tasks = new TaskManager(defaultConfiguration);

        // 逾時時說得出是哪一步（見 OnChainTaskTimeout）。
        // ⚠️ 一定要改 TaskManager 自己那份 DefaultConfiguration，不是傳進去的 defaultConfiguration：
        //    建構子用 With() 複製出另一個物件，事後對傳進去的那個物件指派完全沒有效果。
        tasks.DefaultConfiguration.TimeoutSilently = true;
        tasks.DefaultConfiguration.OnTaskTimeout += OnChainTaskTimeout;
        tasks.DefaultConfiguration.OnTaskException += OnChainTaskException;
        tasks.DefaultConfiguration.OnTaskCompletion += OnChainTaskCompletion;

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

    /// <remarks>
    /// 三條收尾路徑，順序有意義：
    /// <list type="number">
    /// <item><b>非正常中止</b>（逾時、擲例外、回傳 <c>null</c>、被外層延後中止）—— 佇列已被清空，不必再 <c>Abort()</c>；
    /// 但<b>必須先判</b>：清空後的佇列與「全部跑完」分不出來，先判 <see cref="IsMainComplete"/> 就會把失敗當成成功。</item>
    /// <item><b>取消</b> —— <c>BreakIf</c> / <c>RunIf</c> 走的路徑。</item>
    /// <item><b>正常跑完</b>。</item>
    /// </list>
    /// <para>
    /// ⚠️ 前兩條的收尾動作刻意完全一致（<c>OnCancel</c> ＋ <c>OnFinally</c>、
    /// 同樣把 <c>hasTriggeredClosingTasks</c> 設起來讓 <see cref="IsComplete"/> 成立）——
    /// 呼叫端掛在 <c>OnCancel</c> 上的善後（停下尋路、把狀態切回可用）
    /// 在這兩種情況下要做的事情本來就是同一件。
    /// </para>
    /// </remarks>
    private void Tick(IFramework _)
    {
        if (hasTriggeredClosingTasks)
        {
            if (disposeWhenClosed && IsMainComplete())
            {
                Dispose();
            }

            return;
        }

        SettlePendingTimeout();

        if (abortedAbnormally)
        {
            var reason = timedOut ? "timed out" : "was aborted abnormally";
            Logger.Debug($"Chain [{Name}] {reason}.");

            BeginClosing(cancelled: true);
            return;
        }

        if (context.token.IsCancellationRequested && !IsMainComplete())
        {
            Logger.Debug($"Chain [{Name}] was cancelled.");
            tasks.Abort();

            BeginClosing(cancelled: true);
            return;
        }

        if (IsMainComplete())
        {
            BeginClosing(cancelled: false);
        }
    }

    /// <summary>決定收尾並排進佇列，再當場中止還活著的子動作鏈（它們的取消收尾先於本鏈的收尾跑完）。</summary>
    /// <remarks>
    /// 只在本鏈自己的 <see cref="Tick"/> 呼叫：那裡不在 <c>ChainManager.Tick</c> 走訪佇列的當下，可以跑別人的收尾。
    /// 先排收尾再中止子鏈：子鏈的收尾若回頭 <see cref="Abort"/> 本鏈，排好的收尾會由 <see cref="RunClosingCallbacks"/> 補跑。
    /// 正常跑完時子鏈都已被包裝任務收掉；還剩下的是包裝任務被丟棄的孤兒（例如 <c>AbortOnTimeout = false</c> 的逾時）。
    /// </remarks>
    private void BeginClosing(bool cancelled)
    {
        hasTriggeredClosingTasks = true;
        closingCancelled = cancelled;
        tasks.Enqueue(() => RunClosingStep(main: true, guarded: false));
        tasks.Enqueue(() => RunClosingStep(main: false, guarded: false));

        AbortChildren();
    }

    /// <summary>跑一段收尾（<c>main</c>＝OnCancel 或 OnComplete，否則 OnFinally），每段最多一次。</summary>
    /// <remarks>
    /// <c>guarded = false</c> 是佇列裡的那一份：例外照舊交給 TaskManager（記 log 並中止佇列）。
    /// <c>guarded = true</c> 是 <see cref="Abort"/> 當場補跑的那一份：不在 TaskManager 的 try/catch 內，例外要吃掉。
    /// </remarks>
    private void RunClosingStep(bool main, bool guarded)
    {
        if (main ? closingMainRan : closingFinallyRan)
        {
            return;
        }

        if (main)
        {
            closingMainRan = true;
        }
        else
        {
            closingFinallyRan = true;
        }

        var callback = main ? closingCancelled ? OnCancelCallback : OnCompleteCallback : OnFinallyCallback;
        if (!guarded)
        {
            callback?.Invoke();
            return;
        }

        InvokeClosingCallback(callback, main ? closingCancelled ? "OnCancel" : "OnComplete" : "OnFinally");
    }

    /// <summary>
    /// 排入一個「執行一次就算完成」的步驟。
    /// </summary>
    /// <remarks>
    /// 注意：本多載會吞掉步驟的回傳值（ECommons 把 <c>Action</c> 包成 <c>{ action(); return true; }</c>），
    /// 步驟裡回傳的 <c>false</c> 不會讓它重跑，<c>TimeLimitMS</c> 也不參與 —— 步驟被守衛擋下時的結果是
    /// 「這一輪永久跳過」，不是「下一輪再來」。
    /// <para>
    /// 綁到哪一個 Then 多載，由 lambda 主體「回不回值」決定，不是由「有沒有寫成 lambda」決定：
    /// <list type="bullet">
    /// <item>回 void（<c>ctx =&gt; 回 void 的方法(ctx)</c>）綁本多載，做一次。</item>
    /// <item>回 bool（<c>ctx =&gt; 回 bool 的方法(ctx)</c> 或 <c>{ ...; return b; }</c>）綁 <c>Func&lt;ChainContext, bool?&gt;</c> 多載，會重試到 true。</item>
    /// <item>方法群組 <c>Then(回 bool 的方法)</c> 會編譯失敗 CS0407；用 <c>ctx =&gt; { _ = 方法(ctx); }</c> 繞過會靜默掉回本多載。</item>
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
    /// <list type="number">
    /// <item>與 <c>Then(Action&lt;ChainContext&gt;)</c> 一樣會吞掉回傳值，而且沒有 <c>Func&lt;ChainContext, bool?&gt;</c> 多載：
    /// 回 bool 的 lambda 會靜默綁到本多載並被吞掉。需要重試請改用 <c>ConditionalThen(condition, TaskManagerTask)</c>。</item>
    /// <item>lambda 主體是「建一條子 Chain」時，回傳的 <c>Chain</c> 會被丟掉：外層不會等它，也沒有人 Dispose 它。
    /// 要讓外層等待，請寫成零參數 lambda（<c>() =&gt; Chain.Create()...</c>），那才會綁到帶 <c>Func&lt;Chain&gt;</c> 的多載。</item>
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
    /// 需要重試或等待條件時的入口（回 bool 的 lambda 會自動綁到這裡）。
    /// 本多載沒有 <c>TaskManagerConfiguration</c> 參數，沿用這條鏈的預設值 —— <c>Chain.Create</c> 沒傳自訂設定時
    /// 是 <see cref="DefaultTimeLimitMS"/>（10 分鐘）。到期時 <c>OnChainTaskTimeout</c> 寫一行帶步驟描述的
    /// <c>Warning</c>，接著因為 <c>AbortOnTimeout</c> 預設為 <c>true</c> 而中止整條動作鏈。
    /// 要更短的上限請自己建 <c>new TaskManagerTask(func, new TaskManagerConfiguration { TimeLimitMS = ... })</c>
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

    /// <summary>
    /// 排入一個「跑一整條子動作鏈、等它跑完才算完成」的步驟。
    /// </summary>
    /// <remarks>
    /// 📌 子鏈一生出來就登記進 <see cref="children"/>，跑完當場 <see cref="ReleaseChild"/>
    /// 收掉（它掛的兩個 <c>Framework.Update</c> 才會解開）。包裝任務逾時／擲例外／
    /// 被中止時走不到那一行：外層決定收尾的那一刻（<see cref="Tick"/>／<see cref="Abort"/>）把它中止，
    /// 外層直接被 <see cref="Dispose"/> 時改由它自己下一次 Tick 走取消收尾。
    /// </remarks>
    public Chain Then(Func<Chain> factory, TaskManagerConfiguration? config = null)
    {
        Chain? chain = null;
        return Then(new TaskManagerTask(() =>
        {
            if (chain == null)
            {
                chain = factory();
                AdoptChild(chain);
                Logger.Debug($"Creating chain {chain.Name} from factory");
            }

            if (!chain.IsComplete())
            {
                return false;
            }

            ReleaseChild(chain);
            return true;
        }, config));
    }

    public Chain ConditionalThen(Func<ChainContext, bool> condition, Func<Chain> factory, TaskManagerConfiguration? config = null)
    {
        tasks.Enqueue(() =>
        {
            if (condition(context))
            {
                var chain = factory();
                AdoptChild(chain);
                tasks.InsertMulti(new TaskManagerTask(() =>
                {
                    if (!chain.IsComplete())
                    {
                        return false;
                    }

                    ReleaseChild(chain);
                    return true;
                }, config));
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

    /// <summary>
    /// 立刻中止這條動作鏈：清空佇列、把子動作鏈一起中止、跑取消收尾，最後收掉自己。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 子鏈也要中止：<c>tasks.Abort()</c> 只清掉<b>本鏈</b>的佇列，子鏈有自己的 <c>Framework.Update</c> 訂閱，
    /// 不中止就照樣一步一步跑下去（例：<c>Prowler.Abort()</c> 的 <c>Vnavmesh.Stop()</c> 會讓內層 Prowl 鏈
    /// 監看步驟的條件成立，使用者按下中止，結果回報的是「走到了」）。
    /// </para>
    /// <para>
    /// 收尾要<b>同步</b>跑、不照 <see cref="Tick"/> 那樣排進佇列：呼叫端（<c>Prowl.Redirect</c>）是「Abort 完馬上設定新一輪的狀態」，
    /// 排進佇列的收尾會在之後的幀回過頭來改新一輪的狀態，而且緊接著的 <see cref="Dispose"/> 會把還沒跑的收尾整個丟掉。
    /// </para>
    /// <para>
    /// ⚠️ 收尾裡的例外一定要吃掉：這裡不像 <see cref="Tick"/> 那樣跑在 TaskManager 的
    /// try/catch 內，一個擲例外的 <c>OnCancel</c> 會讓 <see cref="Dispose"/> 整個不執行
    /// （訂閱留著），而且會一路擲回呼叫端。
    /// </para>
    /// </remarks>
    public void Abort()
    {
        Svc.Log.Debug($"Aborting chain [{Name}]");
        tasks.Abort();

        // 先收子鏈：它們各自跑自己的取消收尾（Prowl 掛在 OnCancel 上的 vnavmesh.Stop 在這裡）。
        AbortChildren();

        RunClosingCallbacks();
        Dispose();
    }

    /// <summary>把還活著的子動作鏈整批取出並逐一 <see cref="Abort"/>（同步跑它們的取消收尾）。</summary>
    private void AbortChildren()
    {
        foreach (var child in TakeChildren())
        {
            child.Abort();
        }
    }

    /// <summary>
    /// 外層被 <see cref="Dispose"/> 時還活著的子鏈：清空步驟、記為非正常中止，取消收尾由它自己下一次
    /// <see cref="Tick"/> 排入，跑完自己收掉。已經決定收尾的只等收尾跑完再收掉。
    /// </summary>
    /// <remarks>不當場跑收尾：外層的 Dispose 可能發生在 <c>ChainManager.Tick</c> 走訪佇列的當下。</remarks>
    private void AbortDetached()
    {
        disposeWhenClosed = true;
        if (hasTriggeredClosingTasks)
        {
            return;
        }

        abortedAbnormally = true;
        tasks.Abort();
    }

    /// <summary>
    /// 不在當下跑任何收尾地中止：清空本鏈與子動作鏈的佇列並記為非正常中止，
    /// 取消收尾（<c>OnCancel</c>／<c>OnFinally</c>）由各自下一次 <see cref="Tick"/> 照常排入。
    /// </summary>
    /// <remarks>
    /// 給不能當場跑別人的碼的地方用：<c>ChainManager.Tick</c> 走訪佇列時發生的 <see cref="Dispose"/>。
    /// 子動作鏈只清佇列、不取出，由本鏈下一次 <see cref="Tick"/> 決定收尾時取出中止。只在 framework 執行緒呼叫。
    /// </remarks>
    internal void AbortQuietly()
    {
        if (hasTriggeredClosingTasks)
        {
            return;
        }

        abortedAbnormally = true;
        tasks.Abort();

        List<Chain> live;
        lock (children)
        {
            live = [.. children];
        }

        foreach (var child in live)
        {
            child.AbortQuietly();
        }
    }

    /// <summary>
    /// 同步跑完收尾：還沒決定就當成取消（<c>OnCancel</c> ＋ <c>OnFinally</c>）；
    /// 已經決定、排進佇列卻還沒跑的段落（剛被 <c>tasks.Abort()</c> 清掉的）照原本的種類補跑。每段最多一次。
    /// </summary>
    private void RunClosingCallbacks()
    {
        if (!hasTriggeredClosingTasks)
        {
            hasTriggeredClosingTasks = true;
            closingCancelled = true;
        }

        RunClosingStep(main: true, guarded: true);
        RunClosingStep(main: false, guarded: true);
    }

    private void InvokeClosingCallback(Action? callback, string which)
    {
        try
        {
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warning($"動作鏈 [{Name}] 的 {which} 收尾擲出例外：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>把子動作鏈登記進來，讓它有人負責回收。</summary>
    private void AdoptChild(Chain child)
    {
        lock (children)
        {
            if (!disposed)
            {
                children.Add(child);
                return;
            }
        }

        // 外層已經收掉之後才生出來的子鏈（例如 Abort 與任務 lambda 撞在一起）：
        // 掛進去也沒有人會再走一次回收，當場收乾淨。
        child.Abort();
    }

    /// <summary>子動作鏈跑完了，取消登記並收掉它。</summary>
    private void ReleaseChild(Chain child)
    {
        lock (children)
        {
            children.Remove(child);
        }

        child.Dispose();
    }

    /// <summary>把還活著的子動作鏈整批取出並清空登記（呼叫端負責收掉它們）。</summary>
    private List<Chain> TakeChildren()
    {
        lock (children)
        {
            if (children.Count == 0)
            {
                return [];
            }

            var snapshot = new List<Chain>(children);
            children.Clear();
            return snapshot;
        }
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

    /// <summary>收掉時呼叫一次：跑完、被中止、被外層收掉都會走到。</summary>
    /// <remarks>可能在 <c>ChainManager.Tick</c> 走訪佇列的當下呼叫：回呼裡不可跑別人的收尾，也不可新增佇列。</remarks>
    internal Chain OnDispose(Action callback)
    {
        OnDisposeCallback += callback;
        return this;
    }

    /// <summary>
    /// 動作鏈裡任何一步擲出例外時，記下「這條鏈是被中止的」，並把是哪一步寫進 log。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼要記：見 <see cref="abortedAbnormally"/> 的說明。
    /// ⚠️ 只有「這次例外真的會中止整條鏈」才記。<c>@continue</c> 為 <c>true</c>
    /// （事件要求繼續執行同一步）或實際的 abort 決策是 <c>false</c>
    /// （只丟棄這一步、其餘照跑）都不是失敗收尾。
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="continue"/> 與 <paramref name="abort"/> 是 <c>ref</c>：
    /// 寫它們等於改變 ECommons 的中止行為，這裡<b>只讀不寫</b>。
    /// 本處理器掛在 <c>DefaultConfiguration</c> 上，會比任務自帶的處理器<b>先</b>觸發，
    /// 讀到的是還沒被別人改過的值；任務自帶的處理器若會改這兩個參數，
    /// 這裡的判斷就要改成在 TaskManager 真的 Abort 之後才設旗標。
    /// </para>
    /// <para>
    /// 🔴 Warning 那一行刻意尊重 <c>ShowError</c>：<c>ShowError = false</c> 的任務
    /// 是<b>刻意</b>把例外當成常規控制流的（例：消費端的尋路監看在 vnavmesh 停下來時擲例外
    /// 讓這一輪重來），對它印 Warning 會在
    /// 重試迴圈裡洗版。旗標仍然要設 —— 「不印」與「不算中止」是兩件事。
    /// </para>
    /// </remarks>
    private void OnChainTaskException(TaskManagerTask task, Exception exception, ref bool @continue, ref bool? abort)
    {
        ForgetPendingTimeout(task);
        var doAbort = task.Configuration?.AbortOnError ?? tasks.DefaultConfiguration.AbortOnError ?? true;
        if (abort != null)
        {
            doAbort = abort.Value;
        }

        if (@continue || !doAbort)
        {
            return;
        }

        abortedAbnormally = true;

        // 任務自己帶了處理器就讓路，不要把同一次例外印兩遍（與逾時那條同樣的約定）。
        if (task.Configuration?.OnTaskException != null)
        {
            return;
        }

        var showError = task.Configuration?.ShowError ?? tasks.DefaultConfiguration.ShowError ?? true;
        if (!showError)
        {
            return;
        }

        Logger.Warning(
            $"動作鏈 [{Name}] 任務擲出例外：{DescribeTask(task)}"
            + $"（{exception.GetType().Name}：{exception.Message}），整條動作鏈會被中止，收尾走取消路徑。");
    }

    /// <summary>
    /// 有步驟回傳 <c>null</c>（＝要求中止整條佇列）時，記下「這條鏈是被中止的」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼要記：見 <see cref="abortedAbnormally"/> 的說明。ECommons 對這條路徑只寫進自己的環形緩衝區、不進實機 log，
    /// 所以這裡的 Warning 是唯一的紀錄。
    /// </para>
    /// <para>
    /// 可以掛在 <c>OnTaskCompletion</c> 上：那個事件在「<c>result != false</c>」時觸發（<c>true</c> 與 <c>null</c> 兩種都會），
    /// 而且發生在 <c>Abort()</c> 之前。<c>null</c> 以外一律立刻返回。
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="isCompleted"/> 是 <c>ref</c>，這裡<b>只讀不寫</b>。
    /// 本處理器先於任務自帶的處理器觸發：後者若把 <c>null</c> 改成 <c>true</c>／<c>false</c>，這個旗標就會多設一次。
    /// </para>
    /// <para>
    /// ⚠️ 要中止一條鏈的正規寫法是 <c>BreakIf</c>／<c>RunIf</c>（走 <c>ChainContext</c> 的 CancellationToken），
    /// 步驟回 <c>null</c> 本身就是意外，所以這裡的 Warning 不看 <c>ShowError</c>。
    /// </para>
    /// </remarks>
    private void OnChainTaskCompletion(TaskManagerTask task, ref bool? isCompleted)
    {
        ForgetPendingTimeout(task);
        if (isCompleted != null)
        {
            return;
        }

        abortedAbnormally = true;

        if (task.Configuration?.OnTaskCompletion != null)
        {
            return;
        }

        Logger.Warning(
            $"動作鏈 [{Name}] 步驟要求中止整條佇列（回傳 null）：{DescribeTask(task)}，收尾走取消路徑。");
    }

    /// <summary>
    /// 動作鏈裡任何一步逾時時，先把「是哪一步」寫進 log，再讓 ECommons 照原本的流程往下走。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼自己印：ECommons 的 <c>TaskTimeoutException</c> 不帶任務名，它那行逾時 Warning 是匿名的
    /// （建構子用 <c>TimeoutSilently = true</c> 關掉了），不印就查不出是哪一步清掉了整條動作鏈。
    /// </para>
    /// <para>
    /// 契約：任務自帶 <c>OnTaskTimeout</c> 處理器（例：<c>ChainAddon</c> 的兩支等待介面任務）時這裡讓路 ——
    /// 延長或真的逾時都由它負責印，不要印兩遍。
    /// </para>
    /// <para>
    /// 等級刻意維持 <c>Warning</c>（ECommons 原本就是 Warning）；刻意<b>不</b>改 ECommons（全艦隊共用那一份）。
    /// ⚠️ <paramref name="remainingTimeMS"/> 是 <c>ref</c>：寫它等於偷偷延長逾時，這裡<b>只讀不寫</b>。
    /// ⚠️ 任務把 <c>ExecuteDefaultConfigurationEvents</c> 設成 false 時本處理器不會觸發，
    /// 而它仍然沿用預設的 <c>TimeoutSilently</c> ⇒ 那個任務逾時會沒有人印。
    /// </para>
    /// </remarks>
    private void OnChainTaskTimeout(TaskManagerTask task, ref long remainingTimeMS)
    {
        var limit = task.Configuration?.TimeLimitMS ?? tasks.DefaultConfiguration.TimeLimitMS;
        var abort = task.Configuration?.AbortOnTimeout ?? tasks.DefaultConfiguration.AbortOnTimeout ?? true;

        // 🔴 要在下面「讓路」的 return 之前：讓路只管誰印，中止與否不論誰印都要記。
        //    只記下、不當場設旗標：本處理器先於任務自己的處理器觸發，後者可以延長時限
        //    （BOCCHI 趕路監看補回讓位時間）；真的中止才由 Tick 設旗標、走取消收尾。
        if (abort)
        {
            pendingTimeout = task;
        }

        if (task.Configuration?.OnTaskTimeout != null)
        {
            return;
        }

        Logger.Warning(
            $"動作鏈 [{Name}] 任務逾時：{DescribeTask(task)}，上限 {(limit.HasValue ? limit.Value.ToString() : "?")} ms"
            + (abort ? "，整條動作鏈會被中止，收尾走取消路徑。" : "，只丟棄這一步，其餘步驟繼續。"));
    }

    /// <summary>逾時事件之後，這一步既不是目前任務、也沒有完成或擲例外 ⇒ TaskManager 真的因逾時中止了佇列。</summary>
    /// <remarks>
    /// 只在 <see cref="Tick"/> 判 <see cref="IsMainComplete"/> 之前呼叫（清空後與跑完分不出來）。
    /// 還是目前任務＝處理器延長了時限、這一步照跑。TaskManager 的 Tick 比本鏈先訂閱，通常同一幀就判定。
    /// </remarks>
    private void SettlePendingTimeout()
    {
        var task = pendingTimeout;
        if (task == null)
        {
            return;
        }

        pendingTimeout = null;
        if (ReferenceEquals(tasks.CurrentTask, task))
        {
            return;
        }

        timedOut = true;
        abortedAbnormally = true;
    }

    // 延長後這一步在同一幀完成、回傳 null 或擲例外：那些路徑自己處理，不算逾時中止。
    private void ForgetPendingTimeout(TaskManagerTask task)
    {
        if (ReferenceEquals(pendingTimeout, task))
        {
            pendingTimeout = null;
        }
    }

    /// <summary>
    /// 盡量把一個任務描述成人看得懂的樣子。
    /// </summary>
    /// <remarks>
    /// 子模組那份 ECommons 已經把編譯器產生的任務名改寫成「類別.外層方法#b__N」（lambda）／「類別.外層方法.區域函式」。
    /// ⚠️ <c>Location</c> 仍是原始的宣告型別名：lambda 是 <c>&lt;&gt;c</c>／<c>&lt;&gt;c__DisplayClassN_M</c>，一律不印。
    /// </remarks>
    private static string DescribeTask(TaskManagerTask task)
    {
        var name = task.Name ?? "";
        var location = task.Location ?? "";
        if (TryGetEnclosingMethod(name, out var enclosing))
        {
            // lambda 的 Location 是編譯器產生的 <>c / <>c__DisplayClassN_M，印出來只是噪音。
            return location.StartsWith("<>", StringComparison.Ordinal)
                ? $"{enclosing}() 內的匿名步驟 [{name}]"
                : $"{enclosing}() 內的匿名步驟 [{name}@{location}]";
        }

        return location.StartsWith("<>", StringComparison.Ordinal) ? $"[{name}]" : $"[{name}@{location}]";
    }

    /// <summary>
    /// 從 <c>類別.外層方法#b__N</c>（ECommons 給 lambda 的名字）取出「類別.外層方法」；
    /// 開頭是角括號的編譯器原名（<c>&lt;外層方法&gt;b__N</c>，ECommons 改寫不了時原樣交回）也認。
    /// </summary>
    /// <remarks>要連 <c>b__</c> 一起認：具名任務的名字也可能含 '#'。</remarks>
    private static bool TryGetEnclosingMethod(string name, out string enclosing)
    {
        enclosing = "";
        if (name.Length >= 3 && name[0] == '<')
        {
            var end = name.IndexOf('>');
            if (end <= 1)
            {
                return false;
            }

            enclosing = name.Substring(1, end - 1);
            return true;
        }

        var hash = name.IndexOf("#b__", StringComparison.Ordinal);
        if (hash <= 0)
        {
            return false;
        }

        enclosing = name[..hash];
        return true;
    }

    /// <remarks>
    /// ⚠️ 冪等：<see cref="Abort"/>、包裝任務的 <see cref="ReleaseChild"/>、
    /// <c>ChainQueue.Tick</c>、<c>ChainQueue.Dispose</c> 與被延後中止的子鏈自己的 <see cref="Tick"/> 都可能走到這裡，
    /// 而那一行 log 與子鏈的回收都不該做第二遍。
    /// 除了自己的 <c>OnDispose</c> 不跑任何收尾（子鏈走 <see cref="AbortDetached"/>，只設旗標），
    /// 所以在 <c>ChainManager.Tick</c> 走訪佇列時呼叫也安全。
    /// </remarks>
    public void Dispose()
    {
        lock (children)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        Svc.Log.Debug($"Disposing chain [{Name}]");

        // 還活著的子鏈不能只收掉（取消收尾會永遠不跑），也不能當場跑別人的收尾：交給它自己下一次 Tick。
        foreach (var child in TakeChildren())
        {
            child.AbortDetached();
        }

        tasks.Dispose();

        var onDispose = OnDisposeCallback;
        OnCancelCallback = null;
        OnCompleteCallback = null;
        OnFinallyCallback = null;
        OnDisposeCallback = null;

        Svc.Framework.Update -= Tick;

        InvokeClosingCallback(onDispose, "OnDispose");
    }
}
