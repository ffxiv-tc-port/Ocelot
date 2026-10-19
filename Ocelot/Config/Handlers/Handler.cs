using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace Ocelot.Config.Handlers;

public abstract class Handler(ModuleConfig self, ConfigAttribute attribute, PropertyInfo property)
{
    protected abstract Type type { get; }

    protected readonly ConfigAttribute attribute = attribute;

    protected readonly PropertyInfo property = property;

    // 內容只有屬性、型別、設定物件三個不變的參照，而且它自己快取標註：每個處理器共用一個，不必每幀新建。
    private RenderContext? cachedContext;

    private RenderContext GetContext()
    {
        return cachedContext ??= new RenderContext(property, type, self);
    }

    public bool HasUnloadedRequiredPlugins(out List<string> unloaded)
    {
        return GetContext().HasUnloadedRequiredPlugins(out unloaded);
    }

    public bool HasLoadedConflictingPlugins(out List<string> loaded)
    {
        return GetContext().HasLoadedConflictingPlugins(out loaded);
    }

    public (bool handled, bool changed) Render()
    {
        var context = GetContext();

        if (!context.IsValid() || !context.ShouldRender())
        {
            return (false, false);
        }

        // 與 OcelotUi.Indent 相同（深度 0 不縮排），改成直接呼叫以免每幀建立閉包。
        var depth = context.GetIndentation();
        if (depth > 0)
        {
            ImGui.Indent(depth);
        }

        context.CustomIcons();

        if (context.IsIllegal())
        {
            context.Illegal();
        }

        if (context.IsExperimental())
        {
            context.Experimental();
        }

        var (handled, changed) = RenderComponent(context);

        context.Tooltip();

        if (depth > 0)
        {
            ImGui.Unindent(depth);
        }

        return (handled, changed);
    }

    protected abstract (bool handled, bool changed) RenderComponent(RenderContext payload);
}
