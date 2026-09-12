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
    /// 🔴 為什麼非有不可：<c>Then(Func&lt;Chain&gt;)</c>／<c>ConditionalThen(…, Func&lt;Chain&gt;, …)</c>
    /// ／<c>SubChain</c> 都是在「外層任務的 lambda 裡」呼叫 <c>factory()</c> 生出一條新的
    /// <see cref="Chain"/>，而那條子鏈的參考只活在那個 lambda 的閉包裡。
    /// 每一條 <see cref="Chain"/> 在建構時掛<b>兩個</b> <c>Svc.Framework.Update</c>
    /// （自己的 <see cref="Tick"/> ＋ 它那顆 <c>TaskManager</c> 的 <c>Tick</c>），
    /// 並把自己登記進 <c>TaskManager.Instances</c>。在這個欄位出現之前沒有任何一條路徑
    /// 會去 <see cref="Dispose"/> 子鏈 —— 跑完之後 <see cref="Tick"/> 第一行
    /// <c>if (hasTriggeredClosingTasks) return;</c> 每幀空轉，一路留到外掛卸載
    /// ⇒ 長時間自動化下訂閱數線性累積。
    /// </para>
    /// <para>
    /// 📌 登記在這裡的子鏈有兩條回收路徑：正常跑完時由包裝任務 <see cref="ReleaseChild"/>
    /// 當場收掉；外層被 <see cref="Abort"/>／<see cref="Dispose"/> 時連同還活著的一起收。
    /// </para>
    /// <para>
    /// ⚠️ 用 <c>lock</c> 而不是裸 <c>List</c>：加入發生在 framework 執行緒（任務 lambda 裡），
    /// 而 <see cref="Abort"/> 可能從 UI／指令路徑進來。裸 <c>List</c> 並行改動的失敗形式
    /// 不是「拿到舊值」而是清單本身壞掉。
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

    private bool hasTriggeredClosingTasks;

    /// <summary>
    /// 有沒有哪一步逾時，而且那次逾時的設定是「連整條動作鏈一起中止」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 為什麼一定要有這個旗標：逾時中止走的是 ECommons <c>TaskManager.Abort()</c>
    /// （<c>TaskManager.cs</c> 的 <c>catch(TaskTimeoutException)</c> 分支），
    /// 而 <c>Abort()</c> 做的事情是 <c>Tasks.Clear()</c> ＋ <c>CurrentTask = null</c>
    /// ⇒ 佇列被清空之後，<see cref="IsMainComplete"/> 與「所有步驟都跑完了」
    /// <b>長得一模一樣</b>。TaskManager 沒有留下任何可以分辨的狀態，
    /// 所以只能由我們自己在逾時的當下記一筆。
    /// </para>
    /// <para>
    /// 🔴 2026-09-11 之前預設 <c>TimeLimitMS</c> 是 <c>int.MaxValue</c>，逾時實務上走不到，
    /// 所以這條路徑的錯誤一直沒有表現出來；改成 10 分鐘上限之後它變成會走到的路徑
    /// ⇒ 逾時被中止的動作鏈會去觸發 <c>OnComplete</c>，也就是把失敗回報成成功。
    /// </para>
    /// <para>
    /// ⚠️ 只有「這次逾時真的會中止整條鏈」才記。<c>AbortOnTimeout = false</c> 的任務
    /// 逾時只丟棄自己那一步、其餘步驟照跑，那不是失敗收尾。
    /// </para>
    /// </remarks>
    private bool timedOut;

    /// <summary>
    /// 這條動作鏈是不是以「非正常」的方式收場的 —— 逾時、步驟擲出例外、
    /// 或步驟回傳 <c>null</c>（要求中止整條佇列）三者之一。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 為什麼三條路徑共用一個旗標：ECommons <c>TaskManager.Tick</c> 對它們做的事情
    /// <b>完全相同</b> —— <c>catch(TaskTimeoutException)</c> 的 <c>Abort()</c>、
    /// <c>catch(Exception)</c> 經 <c>AbortOnError</c> 的 <c>Abort()</c>、
    /// 以及 <c>result == null</c> 的 <c>Abort()</c>（<c>TaskManager.cs</c> 的
    /// <c>:211</c>／<c>:250</c>／<c>:200</c>）。<c>Abort()</c> 做的是
    /// <c>Tasks.Clear()</c> ＋ <c>CurrentTask = null</c>
    /// ⇒ 事後看 <see cref="IsMainComplete"/>，三者與「所有步驟都跑完了」
    /// <b>長得一模一樣</b>，TaskManager 沒有留下任何可以分辨的狀態。
    /// 所以只能在中止發生的當下由我們自己記一筆，否則失敗會被回報成成功
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
    /// 🔴 這個值在 2026-09-11 之前是 <c>int.MaxValue</c>，也就是「永遠不逾時」。
    /// 後果是一個一直回傳 <c>false</c> 的步驟會把整條動作鏈永久卡住 ——
    /// 不逾時、不中止、log 上一個字都不留，使用者看到的只是「自動化突然不動了」。
    /// </para>
    /// <para>
    /// 📌 為什麼是 10 分鐘：本庫裡<b>有上限</b>的等待最長是 <c>180000</c>（3 分鐘），
    /// 出現在四處 —— <c>BOCCHI/Chains/PathfindAndMoveToChain.cs</c>（走路橫跨一整張圖）、
    /// <c>Modules/Automator/FateActivity.cs</c> 與 <c>Modules/Automator/CriticalEncounter.cs</c>
    /// 的尋路監看（追一個會移動的目標），以及 <c>CriticalEncounter.cs</c> 等危命遭遇開打。
    /// 沒自帶設定的子動作鏈可能把其中一個 3 分鐘的步驟包在裡面，再加上傳送與上坐騎，
    /// 合理上界約 200 秒 ⇒ 取 600000 有三倍餘裕，寧可寬也不要誤殺。
    /// </para>
    /// <para>
    /// ⚠️ 真正需要無上限的等待，本庫一律<b>明確</b>寫 <c>TimeLimitMS = int.MaxValue</c>
    /// （<c>Modules/Automator/Activity.cs</c> 的「參與 FATE／危命遭遇直到它結束」，
    /// 以及 <c>RetryChainFactory.Config()</c> 的重試外殼）。那兩處自帶設定，
    /// <b>不受本預設值影響</b>。
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

        // 逾時時說得出是哪一步 —— 理由與限制寫在 OnChainTaskTimeout 的註解裡。
        // ⚠️ 一定要改 TaskManager 自己那份 DefaultConfiguration，不是上面那個區域變數：
        //    建構子做的是 new TaskManagerConfiguration{...}.With(傳進來的)，
        //    事件被複製進另一個物件，事後對傳進去的那個物件指派完全沒有效果。
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
    /// <item><b>逾時</b> —— ECommons 已經在逾時的那一刻替我們清空佇列了
    /// （<c>catch(TaskTimeoutException)</c> 裡的 <c>Abort()</c>），所以這裡不必再 <c>Abort()</c>
    /// 一次，但<b>必須先判</b>：清空後的佇列與「全部跑完」分不出來，
    /// 先判 <see cref="IsMainComplete"/> 就會把逾時當成成功。</item>
    /// <item><b>取消</b> —— <c>BreakIf</c> / <c>RunIf</c> 走的路徑。</item>
    /// <item><b>正常跑完</b>。</item>
    /// </list>
    /// <para>
    /// ⚠️ 逾時與取消的收尾動作刻意完全一致（同樣是清空佇列 ＋ <c>OnCancel</c> ＋ <c>OnFinally</c>、
    /// 同樣把 <c>hasTriggeredClosingTasks</c> 設起來讓 <see cref="IsComplete"/> 成立）——
    /// 呼叫端掛在 <c>OnCancel</c> 上的善後（停下尋路、把狀態切回可用）
    /// 在這兩種情況下要做的事情本來就是同一件。
    /// </para>
    /// <para>
    /// 📌 幀內順序不影響結果：<c>TaskManager</c> 的 <c>Tick</c> 比本方法早註冊到
    /// <c>Framework.Update</c>，所以逾時通常在同一幀就被看到；就算順序反過來，
    /// 那一幀 <c>timedOut</c> 還是 <c>false</c> 而佇列還沒清空
    /// （<see cref="IsMainComplete"/> 為 <c>false</c>），下一幀才走逾時分支，結果相同。
    /// </para>
    /// <para>
    /// 📌 上面第 1 條的觸發條件是「<b>任何</b>非正常中止」，不只逾時：步驟擲出例外
    /// （<c>AbortOnError</c>）與步驟回傳 <c>null</c>（要求中止佇列）在 ECommons 那側
    /// 走的是同一個 <c>Abort()</c>，留下的狀態也和「全部跑完」分不出來，
    /// 所以三者共用 <see cref="abortedAbnormally"/>、共用同一段收尾。
    /// </para>
    /// </remarks>
    private void Tick(IFramework _)
    {
        if (hasTriggeredClosingTasks)
        {
            return;
        }

        if (abortedAbnormally)
        {
            var reason = timedOut ? "timed out" : "was aborted abnormally";
            Logger.Debug($"Chain [{Name}] {reason}.");

            hasTriggeredClosingTasks = true;
            tasks.Enqueue(() => OnCancelCallback?.Invoke());
            tasks.Enqueue(() => OnFinallyCallback?.Invoke());
            return;
        }

        if (context.token.IsCancellationRequested && !IsMainComplete())
        {
            Logger.Debug($"Chain [{Name}] was cancelled.");
            tasks.Abort();

            hasTriggeredClosingTasks = true;
            tasks.Enqueue(() => OnCancelCallback?.Invoke());
            tasks.Enqueue(() => OnFinallyCallback?.Invoke());
            return;
        }

        if (IsMainComplete())
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
    /// 也就是 <see cref="DefaultTimeLimitMS"/>（10 分鐘）。
    /// <para>
    /// 🔴 這個預設在 2026-09-11 之前是 <c>int.MaxValue</c>，無上限重試，而
    /// 2026-09-03 全庫清點:BOCCHI 與 Ocelot 沒有任何一處 <c>Chain.Create</c> 傳入自訂設定,
    /// 所以一直回傳 <c>false</c> 的步驟會把整條 Chain 永遠卡住,不逾時、也不會留下 log。
    /// 現在同樣的情況會在上限到期時逾時：<c>OnChainTaskTimeout</c> 寫一行帶步驟描述的
    /// <c>Warning</c>，接著因為 <c>AbortOnTimeout</c> 預設為 <c>true</c> 而中止整條動作鏈。
    /// </para>
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

    /// <summary>
    /// 排入一個「跑一整條子動作鏈、等它跑完才算完成」的步驟。
    /// </summary>
    /// <remarks>
    /// 📌 子鏈一生出來就登記進 <see cref="children"/>，跑完當場 <see cref="ReleaseChild"/>
    /// 收掉（它掛的兩個 <c>Framework.Update</c> 才會解開）。包裝任務逾時／擲例外／
    /// 被中止時走不到那一行，那種情況由外層的 <see cref="Abort"/>／<see cref="Dispose"/> 兜底。
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
    /// 🔴 為什麼要把子鏈也中止：<c>tasks.Abort()</c> 只清掉<b>本鏈</b>的佇列，
    /// 包在 <c>Then(Func&lt;Chain&gt;)</c> 裡的那條子鏈有自己的 <c>Framework.Update</c>
    /// 訂閱，外層被清掉之後它<b>照樣一步一步跑下去</b>。
    /// 實例：<c>Prowler.Abort()</c> 先 <c>Vnavmesh.Stop()</c> 再 <c>ChainQueue.Abort()</c>，
    /// 而內層 Prowl 鏈正卡在「Following Path」那個監看步驟上 ——
    /// 它的條件是 <c>!vnavmesh.IsRunning()</c>，剛剛那個 <c>Stop()</c> 讓它<b>成立</b>
    /// ⇒ 內層一路跑到 <c>OnComplete</c>、把 <c>State</c> 設成 <c>Complete</c>。
    /// <b>使用者按下中止，結果回報的是「走到了」。</b>
    /// </para>
    /// <para>
    /// 🔴 為什麼收尾要<b>同步</b>跑而不是照 <see cref="Tick"/> 那樣排進佇列：
    /// 呼叫端（<c>Prowl.Redirect</c>）的寫法是「Abort 完馬上設定新一輪的狀態」，
    /// 排進佇列的收尾會在<b>之後</b>的幀才跑，等於舊的那一輪回過頭來改新一輪的狀態；
    /// 而且緊接著的 <see cref="Dispose"/> 會把那些還沒跑的收尾整個丟掉。
    /// 同步跑保證「舊的收尾全部發生在 Abort 回來之前」。
    /// </para>
    /// <para>
    /// ⚠️ 收尾裡的例外一定要吃掉：這裡不像 <see cref="Tick"/> 那樣跑在 TaskManager 的
    /// try/catch 內，一個擲例外的 <c>OnCancel</c> 會讓 <see cref="Dispose"/> 整個不執行
    /// （訂閱留著＝原本要修的洩漏又回來），而且會一路擲回呼叫端。
    /// </para>
    /// </remarks>
    public void Abort()
    {
        Svc.Log.Info($"Aborting chain [{Name}]");
        tasks.Abort();

        // 先收子鏈：它們各自跑自己的取消收尾（Prowl 掛在 OnCancel 上的 vnavmesh.Stop 在這裡）。
        foreach (var child in TakeChildren())
        {
            child.Abort();
        }

        RunClosingCallbacks();
        Dispose();
    }

    /// <summary>同步跑一次「取消」收尾（<c>OnCancel</c> ＋ <c>OnFinally</c>），最多一次。</summary>
    private void RunClosingCallbacks()
    {
        if (hasTriggeredClosingTasks)
        {
            return;
        }

        hasTriggeredClosingTasks = true;

        InvokeClosingCallback(OnCancelCallback, "OnCancel");
        InvokeClosingCallback(OnFinallyCallback, "OnFinally");
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

    /// <summary>
    /// 動作鏈裡任何一步擲出例外時，記下「這條鏈是被中止的」，並把是哪一步寫進 log。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 由來與逾時那條完全同形：ECommons <c>TaskManager.Tick</c> 的
    /// <c>catch(Exception)</c> 在 <c>AbortOnError</c>（預設 <c>true</c>）時呼叫 <c>Abort()</c>，
    /// 而 <c>Abort()</c> 清空佇列之後，<see cref="IsMainComplete"/> 與「全部跑完」
    /// 分不出來 ⇒ 不記這一筆的話，一個擲例外的步驟會讓整條鏈去觸發 <c>OnComplete</c>。
    /// 實例：<c>Prowl</c> 上坐騎失敗時 <c>throw new Exception("Failed to mount")</c>，
    /// 在這個修正之前整條鏈被清空、後面的 <c>vnavmesh.Stop()</c> 與
    /// <c>State = Complete</c> 都沒跑，而包著它的外層任務看 <see cref="IsComplete"/> 仍然是 <c>true</c>
    /// ⇒ 外層照樣往下走，把這段移動當成完成了。
    /// </para>
    /// <para>
    /// ⚠️ 只有「這次例外真的會中止整條鏈」才記。<c>@continue</c> 為 <c>true</c>
    /// （事件要求繼續執行同一步）或實際的 abort 決策是 <c>false</c>
    /// （只丟棄這一步、其餘照跑）都不是失敗收尾。
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="continue"/> 與 <paramref name="abort"/> 是 <c>ref</c>：
    /// 寫它們等於改變 ECommons 的中止行為，這裡<b>只讀不寫</b>。
    /// 本處理器掛在 <c>DefaultConfiguration</c> 上，會比任務自帶的處理器<b>先</b>觸發，
    /// 所以讀到的是還沒被別人改過的值；哪天有人替某個任務寫了會改這兩個參數的處理器，
    /// 這裡的判斷就要改成在 TaskManager 真的 Abort 之後才設旗標。
    /// Ocelot 與 BOCCHI 目前一個 <c>OnTaskException</c> 處理器都沒有。
    /// </para>
    /// <para>
    /// 🔴 Warning 那一行刻意尊重 <c>ShowError</c>：<c>ShowError = false</c> 的任務
    /// 是<b>刻意</b>把例外當成常規控制流的（本艦隊唯一一處是
    /// <c>BOCCHI/Modules/Automator</c> 的尋路監看，vnavmesh 停下來時擲
    /// <c>VnavmeshStoppedException</c> 讓這一輪重來），對它印 Warning 會在
    /// 重試迴圈裡洗版。旗標仍然要設 —— 「不印」與「不算中止」是兩件事。
    /// </para>
    /// </remarks>
    private void OnChainTaskException(TaskManagerTask task, Exception exception, ref bool @continue, ref bool? abort)
    {
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
    /// 🔴 第三條同形的路徑：<c>TaskManager.Tick</c> 收到 <c>result == null</c> 時直接
    /// <c>Abort()</c>，同樣把佇列清空、同樣與「全部跑完」分不出來。
    /// ECommons 對這條只寫一行 <c>InternalLog.Debug</c>（<c>ShowDebug</c> 預設 <c>false</c>，
    /// 而 <c>InternalLog</c> 只進環形緩衝區不進實機 log）⇒ 在這個修正之前，
    /// 一條被 <c>null</c> 中止的動作鏈會<b>一個字都不留</b>地回報成功。
    /// </para>
    /// <para>
    /// 📌 為什麼可以掛在 <c>OnTaskCompletion</c> 上：那個事件的觸發條件是
    /// 「<c>result != false</c>」，也就是 <c>true</c> 與 <c>null</c> 兩種都會觸發
    /// （<c>TaskManager.cs:178-191</c>），而它就發生在 <c>Abort()</c> 之前。
    /// <c>null</c> 以外一律立刻返回，成本是每個步驟完成時一次 null 檢查。
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="isCompleted"/> 是 <c>ref</c>，這裡<b>只讀不寫</b>。
    /// 與上面同理：本處理器先跑，任務自帶的處理器若把 <c>null</c> 改成 <c>true</c>／<c>false</c>，
    /// 這個旗標就會多設一次。Ocelot 與 BOCCHI 目前一個 <c>OnTaskCompletion</c> 處理器都沒有。
    /// </para>
    /// <para>
    /// ⚠️ 本庫沒有任何步驟是<b>刻意</b>回 <c>null</c> 的 —— 要中止一條鏈的正規寫法是
    /// <c>BreakIf</c>／<c>RunIf</c>（走 <c>ChainContext</c> 的 CancellationToken）。
    /// 所以這裡的 Warning 不設閘門：走到這條路徑本身就是意外。
    /// </para>
    /// </remarks>
    private void OnChainTaskCompletion(TaskManagerTask task, ref bool? isCompleted)
    {
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
    /// 由來：ECommons 的 <c>TaskTimeoutException</c> 是一個<b>完全空的類別</b>
    /// （<c>ECommons/Automation/NeoTaskManager/TaskTimeoutException.cs</c>：
    /// <c>public class TaskTimeoutException : Exception { }</c>，連 Message 都沒有），
    /// 逾時時印出去的 <c>e.LogWarning()</c> 只有空訊息 ＋ 永遠指向 <c>TaskManager.Tick</c> 的堆疊
    /// ⇒ <b>完全匿名</b>。唯一帶任務名的那一行被 <c>ShowDebug</c> 閘住，而這裡從來沒有開過。
    /// ⇒ FATE 自動戰鬥主迴圈、尋寶、狩獵列車任何一步等不到條件而
    /// <c>AbortOnTimeout</c>（預設 true）清掉整條動作鏈時，log 上查不出是哪一步。
    /// </para>
    /// <para>
    /// 📌 <c>ChainAddon</c> 的兩支等待介面任務自己帶了處理器（會印出在等哪一個介面），
    /// 那種情況這裡直接讓路，不要印兩遍。
    /// </para>
    /// <para>
    /// 🔴 等級刻意維持 <c>Warning</c>：ECommons 原本就是 Warning，降級只會弱化訊號。
    /// 🔴 刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那一份。
    /// ⚠️ <paramref name="remainingTimeMS"/> 是 <c>ref</c>：寫它等於偷偷延長逾時，這裡<b>只讀不寫</b>。
    /// ⚠️ <c>TimeoutSilently = true</c> 蓋掉 ECommons 那行匿名 Warning 的前提是
    /// <b>沒有任務把 <c>ExecuteDefaultConfigurationEvents</c> 設成 false</b>
    /// （設了的話預設事件不會觸發，就會變成「靜默 ＋ 沒有人印」）。
    /// Ocelot 與 BOCCHI 目前一處都沒有用到那個旗標。
    /// </para>
    /// </remarks>
    private void OnChainTaskTimeout(TaskManagerTask task, ref long remainingTimeMS)
    {
        var limit = task.Configuration?.TimeLimitMS ?? tasks.DefaultConfiguration.TimeLimitMS;
        var abort = task.Configuration?.AbortOnTimeout ?? tasks.DefaultConfiguration.AbortOnTimeout ?? true;

        // 🔴 這一行要在下面那個「讓路」的 return 之前：讓路只是為了不要把同一次逾時印兩遍，
        //    但不論是誰負責印，整條動作鏈被中止這件事都一樣發生，收尾也一樣要走取消路徑。
        //    ⚠️ 本庫沒有任何 OnTaskTimeout 處理器會去改 remainingTimeMS（改它等於偷偷延長逾時），
        //    所以「逾時事件觸發過」與「真的會中止」在這裡是等價的；哪天有人寫了會延長的處理器，
        //    這個旗標就要改成在 TaskManager 真的 Abort 之後才設。
        if (abort)
        {
            timedOut = true;
            abortedAbnormally = true;
        }

        if (task.Configuration?.OnTaskTimeout != null)
        {
            return;
        }

        Logger.Warning(
            $"動作鏈 [{Name}] 任務逾時：{DescribeTask(task)}，上限 {(limit.HasValue ? limit.Value.ToString() : "?")} ms"
            + (abort ? "，整條動作鏈會被中止，收尾走取消路徑。" : "，只丟棄這一步，其餘步驟繼續。"));
    }

    /// <summary>
    /// 盡量把一個任務描述成人看得懂的樣子。
    /// </summary>
    /// <remarks>
    /// 🔑 2026-09-10 用編譯器實測（net9 / Roslyn）確認過這三種形狀，<b>不要憑印象推</b>：
    /// <list type="bullet">
    /// <item>方法群組：<c>Name = MethodGroupTarget</c>、<c>Location = Outer</c> —— 兩個都有用。</item>
    /// <item>lambda：<c>Name = &lt;Run&gt;b__2_0</c>、<c>Location = &lt;&gt;c</c> 或
    /// <c>&lt;&gt;c__DisplayClass2_0</c> —— <b><c>Location</c> 裡一個類別名都沒有</b>，
    /// 有用的資訊反而在 <c>Name</c> 的角括號裡（外層方法名）。</item>
    /// <item>區域函式：<c>Name = &lt;Run&gt;g__Local|2_3</c>、<c>Location = Outer</c>。</item>
    /// </list>
    /// ⚠️ 動作鏈的步驟絕大多數是 lambda，所以這仍然只是「從完全查不出來」變成
    /// 「查得到是哪個方法／哪個檔」，不是「查得到是第幾行」。
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

        return $"[{name}@{location}]";
    }

    /// <summary>從 <c>&lt;外層方法&gt;b__N</c> / <c>&lt;外層方法&gt;g__名字|N_M</c> 取出外層方法名。</summary>
    private static bool TryGetEnclosingMethod(string name, out string enclosing)
    {
        enclosing = "";
        if (name.Length < 3 || name[0] != '<')
        {
            return false;
        }

        var end = name.IndexOf('>');
        if (end <= 1)
        {
            return false;
        }

        enclosing = name.Substring(1, end - 1);
        return true;
    }

    /// <remarks>
    /// ⚠️ 冪等：<see cref="Abort"/>、包裝任務的 <see cref="ReleaseChild"/>、
    /// <c>ChainQueue.Tick</c> 與 <c>ChainQueue.Dispose</c> 都可能走到這裡，
    /// 而 <c>Svc.Log.Info</c> 與子鏈的回收都不該做第二遍。
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

        Svc.Log.Info($"Disposing chain [{Name}]");

        // 還沒跑完就被收掉的子鏈（外層逾時／擲例外／被中止）在這裡兜底，
        // 否則它們掛的兩個 Framework.Update 會一路留到外掛卸載。
        foreach (var child in TakeChildren())
        {
            child.Dispose();
        }

        tasks.Dispose();

        OnCancelCallback = null;
        OnCompleteCallback = null;
        OnFinallyCallback = null;

        Svc.Framework.Update -= Tick;
    }
}
