using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Interface;
using ECommons.Reflection;
using Dalamud.Bindings.ImGui;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace Ocelot.Config.Handlers;

// 設定畫面每幀對每個屬性都會走一遍：屬性上的標註、翻譯鍵、依賴屬性只查一次（它們在執行期不會變），
// 只有「依賴屬性的值」與「插件在不在」每幀重新看。
public class RenderContext
{
    // 屬性沒有 RequiredPlugin／ConflictingPlugin 時回傳的清單；那時方法回 false，呼叫端不讀它。
    private static readonly List<string> NoPlugins = [];

    private static readonly string WarningIcon = FontAwesomeIcon.ExclamationTriangle.ToIconString();

    private readonly PropertyInfo prop;

    private readonly Type type;

    private readonly ModuleConfig self;

    private Metadata? metadata;

    private string? labelSource;

    private string labelWithId = string.Empty;

    public RenderContext(PropertyInfo prop, Type type, ModuleConfig self)
    {
        this.prop = prop;
        this.type = type;
        this.self = self;
    }

    private Metadata Meta
    {
        get => metadata ??= new Metadata(prop, self.GetType());
    }

    public bool IsValid()
    {
        return prop.PropertyType == type;
    }

    public bool ShouldRender()
    {
        if (HasUnloadedRequiredPlugins(out _))
        {
            return false;
        }

        if (HasLoadedConflictingPlugins(out _))
        {
            return false;
        }

        var dependencies = Meta.DependsOn;
        if (dependencies == null)
        {
            return true;
        }

        foreach (var dependent in dependencies)
        {
            if (dependent == null || dependent.PropertyType != typeof(bool))
            {
                return false;
            }

            if (!(bool)(dependent.GetValue(self) ?? false))
            {
                return false;
            }
        }

        return true;
    }

    public bool HasUnloadedRequiredPlugins(out List<string> unloaded)
    {
        var attr = Meta.RequiredPlugin;
        if (attr == null)
        {
            unloaded = NoPlugins;
            return false;
        }

        unloaded = [];
        foreach (var plugin in attr.dependencies)
        {
            if (!DalamudReflector.TryGetDalamudPlugin(plugin, out _, false, true))
            {
                unloaded.Add(plugin);
            }
        }

        return unloaded.Count > 0;
    }


    public bool HasLoadedConflictingPlugins(out List<string> loaded)
    {
        var attr = Meta.ConflictingPlugin;
        if (attr == null)
        {
            loaded = NoPlugins;
            return false;
        }

        loaded = [];
        foreach (var plugin in attr.conflicts)
        {
            if (DalamudReflector.TryGetDalamudPlugin(plugin, out _, false, true))
            {
                loaded.Add(plugin);
            }
        }

        return loaded.Count > 0;
    }

    public void CustomIcons()
    {
        var iconAttr = Meta.Icon;
        if (iconAttr == null)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, iconAttr.color);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(Meta.IconText);
        ImGui.PopFont();
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(I18N.T(iconAttr.tooltip_translation_key));
        }

        ImGui.SameLine();
    }


    public bool IsExperimental()
    {
        return Meta.Experimental;
    }

    public uint GetIndentation()
    {
        return Meta.Indentation;
    }

    public void Experimental()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.9f, 0.0f, 1.0f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(WarningIcon);
        ImGui.PopFont();
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Experimental");
        }

        ImGui.SameLine();
    }

    public bool IsIllegal()
    {
        return Meta.Illegal;
    }

    public void Illegal()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.2f, 0.2f, 1.0f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(WarningIcon);
        ImGui.PopFont();
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("May lead to a ban. Use at your own risk.");
        }

        ImGui.SameLine();
    }

    public void Tooltip()
    {
        var meta = Meta;
        if (ImGui.IsItemHovered())
        {
            if (meta.TooltipKey == null && !meta.HasLabel)
            {
                ImGui.SetTooltip(self.Owner.T(meta.SnakeTooltipKey));
                return;
            }

            if (meta.TooltipKey != null && !meta.HasLabel)
            {
                ImGui.SetTooltip(I18N.T(meta.TooltipKey));
            }
        }
    }

    public string GetLabel()
    {
        var meta = Meta;
        if (meta.LabelKey != null)
        {
            return I18N.T(meta.LabelKey);
        }

        if (self.Owner == null)
        {
            return "Unknown label";
        }

        return self.Owner.T(meta.SnakeLabelKey);
    }

    // 翻譯字串在語言不變時是同一個實例：同一個實例就沿用上次組好的「標籤##id」。
    public string GetLabelWithId()
    {
        var label = GetLabel();
        if (!ReferenceEquals(label, labelSource))
        {
            labelSource = label;
            labelWithId = $"{label}##{prop.GetHashCode()}";
        }

        return labelWithId;
    }

    public object? GetValue()
    {
        return prop.GetValue(self);
    }

    public void SetValue(object? value)
    {
        prop.SetValue(self, value);
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                // Add underscore if it's not the first character AND
                // (the previous character is lowercase OR the next character is lowercase)
                if (i > 0 && (char.IsLower(input[i - 1]) || i + 1 < input.Length && char.IsLower(input[i + 1])))
                {
                    result.Append('_');
                }

                result.Append(char.ToLower(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private sealed class Metadata
    {
        /// <summary>DependsOn 列出的屬性（依原順序；找不到的是 null）；沒有 DependsOn 時是 null。</summary>
        public readonly PropertyInfo?[]? DependsOn;

        public readonly RequiredPluginAttribute? RequiredPlugin;

        public readonly ConflictingPluginAttribute? ConflictingPlugin;

        public readonly IconAttribute? Icon;

        public readonly string IconText = string.Empty;

        public readonly bool Experimental;

        public readonly bool Illegal;

        public readonly uint Indentation;

        public readonly string? TooltipKey;

        public readonly bool HasLabel;

        public readonly string? LabelKey;

        public readonly string SnakeLabelKey;

        public readonly string SnakeTooltipKey;

        public Metadata(PropertyInfo prop, Type configType)
        {
            var dependsOn = prop.GetCustomAttribute<DependsOnAttribute>();
            if (dependsOn != null)
            {
                DependsOn = new PropertyInfo?[dependsOn.dependencies.Length];
                for (var i = 0; i < DependsOn.Length; i++)
                {
                    DependsOn[i] = configType.GetProperty(dependsOn.dependencies[i]);
                }
            }

            RequiredPlugin = prop.GetCustomAttribute<RequiredPluginAttribute>();
            ConflictingPlugin = prop.GetCustomAttribute<ConflictingPluginAttribute>();
            Icon = prop.GetCustomAttribute<IconAttribute>();
            if (Icon != null)
            {
                IconText = Icon.icon.ToIconString();
            }

            Experimental = prop.GetCustomAttribute<ExperimentalAttribute>() != null;
            Illegal = prop.GetCustomAttribute<IllegalAttribute>() != null;
            Indentation = prop.GetCustomAttribute<IndentAttribute>()?.depth ?? 0;

            var label = prop.GetCustomAttribute<LabelAttribute>();
            var labelAndTooltip = prop.GetCustomAttribute<LabelAndTooltipAttribute>();
            TooltipKey = prop.GetCustomAttribute<TooltipAttribute>()?.translation_key ?? labelAndTooltip?.translation_key;
            HasLabel = label != null;
            LabelKey = label?.translation_key ?? labelAndTooltip?.translation_key;

            var snake = ToSnakeCase(prop.Name);
            SnakeLabelKey = $"config.{snake}.label";
            SnakeTooltipKey = $"config.{snake}.tooltip";
        }
    }
}
