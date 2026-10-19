using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Ocelot.Ui;
using Ocelot.Windows;

namespace Ocelot.Modules;

public class ModuleManager
{
    private readonly List<IModule> modules = new();

    private readonly Dictionary<IModule, int> configOrders = new();

    private readonly Dictionary<IModule, int> mainOrders = new();

    private List<IModule> toUpdate = [];

    private List<IModule> toRender = [];

    // 每幀篩選用的備用清單：填好才與現用的對調，所以篩選期間現用的那份（上一幀的結果）保持原樣，同每幀新建一份時的行為。
    private List<IModule> toUpdateSpare = [];

    private List<IModule> toRenderSpare = [];

    private List<IModule> toInitialize = [];

    // 每幀階段出錯就全停：PostUpdate 依賴 PreUpdate／Update 算出的狀態，不能讓自動化之類的後段拿過期狀態行動；
    // 所以本幀任一模組失敗就略過 PostUpdate，例外收齊後重擲，由 Dalamud 記一次並跳過本外掛之後訂閱的 Framework.Update。
    private readonly List<Exception> frameFailures = [];

    private bool skipPostUpdate;

    // 主視窗用的模組順序與 ID 字串：模組清單只會在註冊時變長，長度變了才重建（每幀排序與組字串是主視窗的主要配置）。
    private IModule[] mainOrder = [];

    private string[] mainIds = [];

    private int mainOrderBuiltFor = -1;

    // 使用者自訂的主視窗順序（模組型別全名）；空＝照 [OcelotModule] 的 MainOrder。
    private string[] customMainOrder = [];

    // GetModule 的結果：清單只會在尾端加，第一個符合的型別永遠是同一個，所以只快取找到的（找不到的之後可能才註冊）。
    // 呼叫端不限框架執行緒，用 ConcurrentDictionary。
    private readonly ConcurrentDictionary<Type, IModule> moduleCache = new();

    public void Add(Module<OcelotPlugin, IOcelotConfig> module)
    {
        modules.Add(module);
    }

    public void AutoRegister(OcelotPlugin plugin, IOcelotConfig config)
    {
        var moduleTypes = Registry
            .GetTypesWithAttributeData<OcelotModuleAttribute>()
            .Where(t => typeof(IModule).IsAssignableFrom(t.type));

        foreach (var (type, attr) in moduleTypes)
        {
            Logger.Info($"Registering module: {type.FullName}");

            // 建構失敗就不登記：沒有實例，後續各階段與 Dispose 都無從呼叫它。
            IModule moduleInstance;
            try
            {
                moduleInstance = (IModule)Activator.CreateInstance(type, plugin, config)!;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Module {Module:l} threw during construction and was not registered; other modules continue", type.FullName ?? type.Name);
                continue;
            }

            modules.Add(moduleInstance);
            if (attr != null)
            {
                configOrders[moduleInstance] = attr.ConfigOrder;
                mainOrders[moduleInstance] = attr.MainOrder;
            }
        }
    }

    /// <summary>這一幀要畫的模組，順序同 <see cref="MainOrder"/>。</summary>
    public IEnumerable<IModule> GetModulesByMainOrder()
    {
        EnsureMainOrder();
        return mainOrder.Where(toRender.Contains);
    }

    /// <summary>主視窗各區段的順序（含這一幀不畫的模組）；只在畫面執行緒讀，不要改它。</summary>
    public IReadOnlyList<IModule> MainOrder
    {
        get
        {
            EnsureMainOrder();
            return mainOrder;
        }
    }

    /// <summary>
    /// 自訂主視窗的區段順序（模組型別全名，由上到下）。null 或空＝照 [OcelotModule] 的 MainOrder（與沒有自訂時逐一相同）。
    /// 清單裡的模組照清單排在最前面；不在清單裡的（例如之後才加的模組）照原本順序接在後面；不認得或重複的名字略過。
    /// 只在畫面執行緒呼叫（同 <see cref="RenderMainUi"/>）。
    /// </summary>
    public void SetMainOrder(IReadOnlyList<string>? typeNames)
    {
        customMainOrder = typeNames is { Count: > 0 } ? [.. typeNames] : [];
        mainOrderBuiltFor = -1;
    }

    public IEnumerable<IModule> GetModulesByConfigOrder()
    {
        return modules.OrderBy(m => configOrders.GetValueOrDefault(m, int.MaxValue));
    }

    public void PreInitialize()
    {
        toInitialize = FilterModules(modules, nameof(IModule.ShouldInitialize), m => m.ShouldInitialize);

        ForEachModule(modules, nameof(ModuleConfig.SetOwner), m => m.Config?.SetOwner(m));
        ForEachModule(toInitialize, nameof(IModule.PreInitialize), m => m.PreInitialize());
    }

    public void Initialize()
    {
        ForEachModule(toInitialize, nameof(IModule.Initialize), m => m.Initialize());
    }

    public void PostInitialize()
    {
        ForEachModule(toInitialize, nameof(IModule.PostInitialize), m => m.PostInitialize());
    }

    public void InjectModules()
    {
        ForEachModule(toInitialize, nameof(IModule.InjectModules), m => m.InjectModules());
    }

    public void InjectIPCs()
    {
        ForEachModule(toInitialize, nameof(IModule.InjectIPCs), m => m.InjectIPCs());
    }

    public void PreUpdate(UpdateContext context)
    {
        frameFailures.Clear();

        FilterInto(toUpdateSpare, modules, nameof(IModule.ShouldUpdate), static (m, c) => m is { ShouldUpdate: true, HasRequiredIPCs: true } && m.UpdateLimit.ShouldUpdate(m, c), context, frameFailures);
        (toUpdate, toUpdateSpare) = (toUpdateSpare, toUpdate);
        ForEachModule(toUpdate, nameof(IModule.PreUpdate), static (m, c) => m.PreUpdate(c), context, frameFailures);

        skipPostUpdate = frameFailures.Count > 0;
    }

    public void Update(UpdateContext context)
    {
        ForEachModule(toUpdate, nameof(IModule.Update), static (m, c) => m.Update(c), context, frameFailures);

        skipPostUpdate |= frameFailures.Count > 0;
        ThrowFrameFailures();
    }

    public void PostUpdate(UpdateContext context)
    {
        if (!skipPostUpdate)
        {
            ForEachModule(toUpdate, nameof(IModule.PostUpdate), static (m, c) => m.PostUpdate(c), context, frameFailures);
        }

        ThrowFrameFailures();
    }

    public void Render(RenderContext context)
    {
        FilterInto(toRenderSpare, modules, nameof(IModule.ShouldRender), static (m, _) => m is { ShouldRender: true, HasRequiredIPCs: true }, context);
        (toRender, toRenderSpare) = (toRenderSpare, toRender);
        ForEachModule(toRender, nameof(IModule.Render), static (m, c) => m.Render(c), context);
    }

    // 刻意不逐模組攔例外：這支跑在 Dalamud Window 的 Draw() 裡，那層會還原樣式堆疊、節流 log 並改畫錯誤面板；
    // 在這裡攔下會在沒配對的 PushID 上接著畫其餘模組，而且模組每幀擲例外就每幀寫一次 log。
    // 分隔線只畫在兩個有畫東西的模組之間：要等模組畫完才知道它有沒有東西，所以先在它前面保留分隔線的位置，
    // 它沒畫（游標也沒動）就把游標退回去 ⇒ 最後一個有畫的模組下面不會多一條線。順序與 GetModulesByMainOrder 相同。
    public void RenderMainUi(RenderContext context)
    {
        EnsureMainOrder();

        var anyRendered = false;
        for (var i = 0; i < mainOrder.Length; i++)
        {
            var module = mainOrder[i];
            if (!toRender.Contains(module))
            {
                continue;
            }

            var slotY = 0f;
            var lineStart = Vector2.Zero;
            var lineWidth = 0f;
            if (anyRendered)
            {
                slotY = ImGui.GetCursorPosY();
                lineStart = ImGui.GetCursorScreenPos();
                lineWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                ImGui.Dummy(new Vector2(lineWidth, 1));
            }

            var afterSlotY = ImGui.GetCursorPosY();
            ImGui.PushID(mainIds[i]);
            var rendered = module.RenderMainUi(context);
            ImGui.PopID();

            if (rendered)
            {
                if (anyRendered)
                {
                    ImGui.GetWindowDrawList().AddLine(lineStart, new Vector2(lineStart.X + lineWidth, lineStart.Y), ImGui.GetColorU32(ImGuiCol.Separator));
                }

                OcelotUi.VSpace();
                anyRendered = true;
            }
            else if (anyRendered && ImGui.GetCursorPosY() == afterSlotY)
            {
                ImGui.SetCursorPosY(slotY);
            }
        }
    }

    private void EnsureMainOrder()
    {
        if (mainOrderBuiltFor == modules.Count)
        {
            return;
        }

        var ordered = modules.OrderBy(m => mainOrders.GetValueOrDefault(m, int.MaxValue)).ToList();
        if (customMainOrder.Length > 0)
        {
            var custom = new List<IModule>(ordered.Count);
            foreach (var name in customMainOrder)
            {
                var index = ordered.FindIndex(m => m.GetType().FullName == name);
                if (index >= 0)
                {
                    custom.Add(ordered[index]);
                    ordered.RemoveAt(index);
                }
            }

            custom.AddRange(ordered);
            ordered = custom;
        }

        mainOrder = ordered.ToArray();
        mainIds = new string[mainOrder.Length];
        for (var i = 0; i < mainOrder.Length; i++)
        {
            mainIds[i] = $"OcelotMain##{mainOrder[i].GetType().FullName}";
        }

        mainOrderBuiltFor = modules.Count;
    }

    // 同 RenderMainUi：刻意不逐模組攔例外。
    public void RenderConfigUi(RenderContext context)
    {
        var orderedModules = GetModulesByConfigOrder().ToList();
        foreach (var module in orderedModules)
        {
            module.RenderConfigUi(context);
            OcelotUi.VSpace();
            if (module != orderedModules.Last())
            {
                OcelotUi.Separator();
            }
        }
    }

    public void OnChatMessage(XivChatType type, int timestamp, SeString sender, SeString message, bool isHandled)
    {
        ForEachModule(toUpdate, nameof(IModule.OnChatMessage), m => m.OnChatMessage(type, timestamp, sender, message, isHandled));
    }

    public void OnTerritoryChanged(ushort id)
    {
        ForEachModule(toUpdate, nameof(IModule.OnTerritoryChanged), m => m.OnTerritoryChanged(id));
    }

    public T GetModule<T>() where T : class, IModule
    {
        if (moduleCache.TryGetValue(typeof(T), out var cached))
        {
            return (T)cached;
        }

        var module = modules.OfType<T>().FirstOrDefault();
        if (module == null)
        {
            throw new UnableToLoadModuleException($"Module of type {typeof(T).Name} was not found.");
        }

        moduleCache.TryAdd(typeof(T), module);
        return module;
    }

    public bool TryGetModule<T>(out T? module) where T : class, IModule
    {
        try
        {
            module = GetModule<T>();
            return true;
        }
        catch (UnableToLoadModuleException ex)
        {
            Logger.Error(ex.Message);
            module = null;
            return false;
        }
    }

    public void Dispose()
    {
        ForEachModule(modules, nameof(IDisposable.Dispose), m => m.Dispose());
    }

    // 契約：每個模組各自 try/catch，一個模組擲例外只中止它自己這一次呼叫；
    // 給了 failures 就收進去交給呼叫端重擲（這裡不記 log），沒給就當場寫 Error。
    private static void ForEachModule(List<IModule> targets, string phase, Action<IModule> action, List<Exception>? failures = null)
    {
        foreach (var module in targets)
        {
            try
            {
                action(module);
            }
            catch (Exception ex)
            {
                HandleModuleException(module, phase, ex, failures);
            }
        }
    }

    // 每幀階段用：同上，但把 state 當參數傳給 static lambda，不必每幀為了捕捉 context 建閉包與委派。
    private static void ForEachModule<TState>(List<IModule> targets, string phase, Action<IModule, TState> action, TState state, List<Exception>? failures = null)
    {
        foreach (var module in targets)
        {
            try
            {
                action(module, state);
            }
            catch (Exception ex)
            {
                HandleModuleException(module, phase, ex, failures);
            }
        }
    }

    // 每幀階段用的 FilterModules：結果填進呼叫端給的清單（先清空），不新建。
    private static void FilterInto<TState>(List<IModule> selected, List<IModule> source, string phase, Func<IModule, TState, bool> predicate, TState state, List<Exception>? failures = null)
    {
        selected.Clear();
        foreach (var module in source)
        {
            try
            {
                if (predicate(module, state))
                {
                    selected.Add(module);
                }
            }
            catch (Exception ex)
            {
                HandleModuleException(module, phase, ex, failures);
            }
        }
    }

    // 篩選條件也是模組自己的碼：擲例外的模組只在這一輪視同不符合，例外的處理同 ForEachModule。
    private static List<IModule> FilterModules(List<IModule> source, string phase, Func<IModule, bool> predicate, List<Exception>? failures = null)
    {
        var selected = new List<IModule>(source.Count);
        foreach (var module in source)
        {
            try
            {
                if (predicate(module))
                {
                    selected.Add(module);
                }
            }
            catch (Exception ex)
            {
                HandleModuleException(module, phase, ex, failures);
            }
        }

        return selected;
    }

    private static void HandleModuleException(IModule? module, string phase, Exception ex, List<Exception>? failures)
    {
        var name = module?.GetType().FullName ?? "<null>";
        if (failures != null)
        {
            failures.Add(new ModuleFailureException($"Module {name} threw during {phase}", ex));
            return;
        }

        Logger.Error(ex, "Module {Module:l} threw during {Phase:l}; other modules continue", name, phase);
    }

    private void ThrowFrameFailures()
    {
        if (frameFailures.Count == 0)
        {
            return;
        }

        var failures = frameFailures.ToArray();
        frameFailures.Clear();
        throw new AggregateException(skipPostUpdate ? "Module update failed this frame; PostUpdate skipped" : "Module PostUpdate failed this frame", failures);
    }

    private sealed class ModuleFailureException(string message, Exception innerException) : Exception(message, innerException);
}
