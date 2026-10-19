using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ECommons.Reflection;
using Dalamud.Bindings.ImGui;
using Ocelot.Config.Attributes;
using Ocelot.Config.Handlers;
using Ocelot.Ui;

namespace Ocelot.Modules;

[Serializable]
public abstract class ModuleConfig
{
    [IgnoreDataMember]
    public virtual string ProviderNamespace
    {
        get => GetType().Namespace ?? string.Empty;
    }

    [IgnoreDataMember] public IModule Owner { get; private set; } = null!;

    internal void SetOwner(IModule module)
    {
        Logger.Info($"Set owner to type of {module.GetType().Name}");
        Owner = module;
    }

    private List<Handler>? handlers;

    private IEnumerable<Handler> GetHandlers()
    {
        if (this.handlers != null)
        {
            return this.handlers;
        }

        var handlers = new List<Handler>();
        var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var prop in props)
        {
            var configAttrs = prop.GetCustomAttributes<ConfigAttribute>(true);

            foreach (var attr in configAttrs)
            {
                Logger.Info($"Property: {prop.Name}, Attribute: {attr.GetType().Name}, Handler: {attr.GetHandler(this, attr, prop)?.GetType().Name}");
                handlers.Add(attr.GetHandler(this, attr, prop));
            }
        }

        if (props.Length == 0)
        {
            Logger.Info($"No properties found for config type: {GetType().Name}");
        }

        this.handlers = handlers;
        return handlers;
    }

    // 設定畫面每幀都會畫：型別上的標註與翻譯鍵只查一次，翻譯本身照舊每次查（語言可能改）。
    private string? titleKey;

    private string[]? textKeys;

    private string? regionId;

    private bool typeAttributesRead;

    private RequiredPluginAttribute? typeRequiredPlugin;

    private ConflictingPluginAttribute? typeConflictingPlugin;

    public string? GetTitle()
    {
        titleKey ??= (GetType().GetCustomAttribute<TitleAttribute>() ?? new TitleAttribute()).TranslationKey;

        return Owner.T(titleKey);
    }

    public List<string> GetText()
    {
        List<string> output = [];
        foreach (var key in GetTextKeys())
        {
            output.Add(Owner.T(key));
        }

        return output;
    }

    private string[] GetTextKeys()
    {
        return textKeys ??= GetType().GetCustomAttributes<TextAttribute>().Select(text => text.translation_key).ToArray();
    }

    public bool Draw()
    {
        // 與 OcelotUi.Region 相同（PushID→內容→PopID），改成直接呼叫以免每幀建立閉包與 id 字串。
        ImGui.PushID(regionId ??= $"OcelotConfig##{GetType().FullName}");
        var dirty = DrawContents();
        ImGui.PopID();

        return dirty;
    }

    private bool DrawContents()
    {
        var title = GetTitle();
        if (title != null)
        {
            OcelotUi.Title(title);
        }

        foreach (var key in GetTextKeys())
        {
            ImGui.TextUnformatted(Owner.T(key));
        }

        if (!typeAttributesRead)
        {
            typeRequiredPlugin = GetType().GetCustomAttribute<RequiredPluginAttribute>();
            typeConflictingPlugin = GetType().GetCustomAttribute<ConflictingPluginAttribute>();
            typeAttributesRead = true;
        }

        var error = false;
        var requiredPlugin = typeRequiredPlugin;
        if (requiredPlugin != null)
        {
            List<string> missingClass = [];
            foreach (var plugin in requiredPlugin.dependencies)
            {
                if (!DalamudReflector.TryGetDalamudPlugin(plugin, out _, false, true))
                {
                    missingClass.Add(plugin);
                }
            }

            if (missingClass.Count > 0)
            {
                error = true;
                DrawPluginNotice("The following plugins are required for this module:", missingClass);
            }
        }

        var conflictingPlugin = typeConflictingPlugin;
        if (conflictingPlugin != null)
        {
            List<string> conflictingClass = [];
            foreach (var plugin in conflictingPlugin.conflicts)
            {
                if (DalamudReflector.TryGetDalamudPlugin(plugin, out _, false, true))
                {
                    conflictingClass.Add(plugin);
                }
            }

            if (conflictingClass.Count > 0)
            {
                error = true;
                DrawPluginNotice("The following plugins are conflicting with this module:", conflictingClass);
            }
        }

        if (error)
        {
            return false;
        }

        List<string>? missingAttrs = null;
        foreach (var handler in GetHandlers())
        {
            if (handler.HasUnloadedRequiredPlugins(out var unloaded))
            {
                missingAttrs ??= [];
                foreach (var item in unloaded)
                {
                    if (!missingAttrs.Contains(item))
                    {
                        missingAttrs.Add(item);
                    }
                }
            }
        }

        if (missingAttrs is { Count: > 0 })
        {
            DrawPluginNotice("Some options are hidden due to missing plugins:", missingAttrs);
        }

        List<string>? conflictingAttrs = null;
        foreach (var handler in GetHandlers())
        {
            if (handler.HasLoadedConflictingPlugins(out var loaded))
            {
                conflictingAttrs ??= [];
                foreach (var item in loaded)
                {
                    if (!conflictingAttrs.Contains(item))
                    {
                        conflictingAttrs.Add(item);
                    }
                }
            }
        }

        if (conflictingAttrs is { Count: > 0 })
        {
            DrawPluginNotice("Some options are hidden due to conflicting plugins: ", conflictingAttrs);
        }

        // 與 OcelotUi.Indent(16, …) 相同，直接呼叫以免每幀建立閉包。
        var dirty = false;
        ImGui.Indent(16);
        foreach (var handler in GetHandlers())
        {
            var (handled, changed) = handler.Render();
            if (handled && changed)
            {
                dirty = true;
            }
        }

        ImGui.Unindent(16);

        return dirty;
    }

    // 與 OcelotUi.Indent(() => …) 相同；寫成方法而不是 lambda：捕獲方法層級區域變數的閉包在方法一進來就配置，每幀都付。
    private static void DrawPluginNotice(string message, List<string> plugins)
    {
        ImGui.Indent(16);
        OcelotUi.Error(message);
        ImGui.SameLine();
        ImGui.TextUnformatted(string.Join(", ", plugins));
        ImGui.Unindent(16);
    }

    // 模組的 IsEnabled／ShouldXxx 每幀都會問這個：屬性與 DependsOn 只解析一次，之後直接呼叫屬性的 getter，
    // 免掉每次反射查屬性、讀標註、把 bool 裝箱；也可能從別的執行緒問，所以用併行字典。
    private ConcurrentDictionary<string, EnabledCheck>? enabledChecks;

    public bool IsPropertyEnabled(string propertyName)
    {
        var checks = enabledChecks ??= new ConcurrentDictionary<string, EnabledCheck>();
        var check = checks.GetOrAdd(propertyName, static (name, type) => EnabledCheck.Create(type, name), GetType());

        return check.Evaluate(this);
    }

    private sealed class EnabledCheck
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly MethodInfo MakeGetterMethod = typeof(EnabledCheck).GetMethod(nameof(MakeGetter), BindingFlags.NonPublic | BindingFlags.Static)!;

        // null＝屬性不存在或不是 bool：一律回 false。
        private readonly Func<ModuleConfig, bool>? property;

        // DependsOn 列出的屬性依原順序；某一個是 null（不存在或不是 bool）時一律回 false。
        private readonly Func<ModuleConfig, bool>?[] dependencies;

        private EnabledCheck(Func<ModuleConfig, bool>? property, Func<ModuleConfig, bool>?[] dependencies)
        {
            this.property = property;
            this.dependencies = dependencies;
        }

        public static EnabledCheck Create(Type type, string name)
        {
            var prop = type.GetProperty(name, Flags);
            if (prop == null || prop.PropertyType != typeof(bool))
            {
                return new EnabledCheck(null, []);
            }

            var dependsOn = prop.GetCustomAttribute<DependsOnAttribute>();
            var dependencies = new Func<ModuleConfig, bool>?[dependsOn?.dependencies.Length ?? 0];
            for (var i = 0; i < dependencies.Length; i++)
            {
                var depProp = type.GetProperty(dependsOn!.dependencies[i], Flags);
                dependencies[i] = depProp?.PropertyType == typeof(bool) ? Getter(type, depProp) : null;
            }

            return new EnabledCheck(Getter(type, prop), dependencies);
        }

        public bool Evaluate(ModuleConfig config)
        {
            if (property == null)
            {
                return false;
            }

            foreach (var dependency in dependencies)
            {
                if (dependency == null || !dependency(config))
                {
                    return false;
                }
            }

            return property(config);
        }

        private static Func<ModuleConfig, bool> Getter(Type type, PropertyInfo prop)
        {
            var getter = prop.GetGetMethod(true);
            if (getter == null || getter.IsStatic)
            {
                // 沒有實例 getter 時退回 prop.GetValue：沒有 getter 的屬性照樣在讀取的那一刻擲例外。
                return config => (bool)(prop.GetValue(config) ?? false);
            }

            return (Func<ModuleConfig, bool>)MakeGetterMethod.MakeGenericMethod(getter.DeclaringType ?? type).Invoke(null, [getter])!;
        }

        private static Func<ModuleConfig, bool> MakeGetter<T>(MethodInfo getter) where T : class
        {
            var typed = getter.CreateDelegate<Func<T, bool>>();
            return config => typed((T)(object)config);
        }
    }
}
