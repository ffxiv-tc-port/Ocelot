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
        if (task.Configuration?.OnTaskTimeout != null)
        {
            return;
        }

        var limit = task.Configuration?.TimeLimitMS ?? tasks.DefaultConfiguration.TimeLimitMS;
        var abort = task.Configuration?.AbortOnTimeout ?? tasks.DefaultConfiguration.AbortOnTimeout ?? true;
        Logger.Warning(
            $"動作鏈 [{Name}] 任務逾時：{DescribeTask(task)}，上限 {(limit.HasValue ? limit.Value.ToString() : "?")} ms"
            + (abort ? "，整條動作鏈會被中止。" : "，只丟棄這一步，其餘步驟繼續。"));
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
