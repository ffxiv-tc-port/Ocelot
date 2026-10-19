using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using Ocelot.Chain;
using Ocelot.Commands;
using Ocelot.Data;
using Ocelot.Debug;
using Ocelot.IPC;
using Ocelot.Modules;
using Ocelot.Windows;
using Pictomancy;
using AtkUnitBase = FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Ocelot;

public abstract class OcelotPlugin : IDalamudPlugin
{
    public abstract string Name { get; }

    public virtual string Version
    {
        get => Svc.PluginInterface.Manifest.AssemblyVersion.ToString();
    }

    public const string OcelotVersion = "1.1.1";

    public abstract IOcelotConfig OcelotConfig { get; }

    public readonly ModuleManager Modules = new();

    public readonly WindowManager Windows = new();

    public readonly CommandManager Commands = new();

    public readonly IPCManager IPC = new();

    protected readonly PluginWatcher PluginWatcher;

    public RenderContext? RenderContext { get; private set; } = null;

    // 每幀沿用同一個：它不帶逐幀狀態，本幀的繪製清單由 OverlayDrawList() 給。
    private RenderContext? sharedRenderContext;

    // 本幀已向 Pictomancy 建立的繪製清單；null＝這一幀（還）沒有人要畫世界疊加層。
    private PctDrawList? frameDrawList;

    // PreRender 到 PostRender 之間、而且這一幀可以畫。
    private bool overlayFrameOpen;

    private OverlayMode overlayMode;

    // Probing＝每幀都先建繪製清單（原本的做法），直到建成功一次、確認 Pictomancy 的繪製器可用；
    // Deferred＝等有人要畫才建；Disabled＝延後時發現閘門判斷與 Pictomancy 不一致，永久回到每幀都建。
    private enum OverlayMode
    {
        Probing,

        Deferred,

        Disabled,
    }

    protected OcelotPlugin(IDalamudPluginInterface plugin, params Module[] eModules)
    {
        ECommonsMain.Init(plugin, this, eModules);
        PictoService.Initialize(plugin);

        Registry.RegisterAssemblies(typeof(OcelotPlugin).Assembly);
        Registry.RegisterAssemblies(GetType().Assembly);

        PluginWatcher = new PluginWatcher();
    }

    protected void OcelotInitialize(params OcelotFeature[] features)
    {
        Svc.PluginInterface.UiBuilder.Draw += PreRender;

        OcelotFeatureEx.SetFeatures(features.Length <= 0 ? [OcelotFeature.All] : features);

        if (OcelotFeature.IPC.IsEnabled())
        {
            Logger.Info("Initializing IPC Manager...");
            IPC.Initialize();
        }

        if (OcelotFeature.ModuleManager.IsEnabled())
        {
            Logger.Info("Initializing Module Manager...");
            Modules.AutoRegister(this, OcelotConfig);
            Modules.PreInitialize();
            Modules.Initialize();
            Svc.PluginInterface.UiBuilder.Draw += Render;
        }

        if (OcelotFeature.WindowManager.IsEnabled())
        {
            Logger.Info("Initializing Window Manager...");
            Windows.Initialize(this, OcelotConfig);
        }

        if (OcelotFeature.CommandManager.IsEnabled())
        {
            Logger.Info("Initializing Command Manager...");
            Commands.Initialize(this);
        }

        if (OcelotFeature.Prowler.IsEnabled() && OcelotFeature.IPC.IsEnabled())
        {
            Logger.Info("Initializing Prowler...");
            Prowler.Prowler.Initialize(this);
        }

        if (OcelotFeature.ChainManager.IsEnabled())
        {
            ChainManager.Initialize();
        }

        Modules.PostInitialize();
        Modules.InjectModules();
        Modules.InjectIPCs();

        Svc.Framework.Update += Update;
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;

        Svc.PluginInterface.UiBuilder.Draw += PostRender;

        PluginWatcher.OnPluginListChanged += OnPluginListChanged;

        if (PluginWatcher.IsOcelotPluginEnabled(OcelotPlugins.OcelotMonitor))
        {
            IPC.AddProvider(new DebugIPCProvider(this));
        }
    }

    protected virtual bool ShouldUpdate()
    {
        return true;
    }

    protected virtual void Update(IFramework framework)
    {
        if (!ShouldUpdate())
        {
            return;
        }

        var context = new UpdateContext(framework, this);

        PluginWatcher.Update(context);

        Modules.PreUpdate(context);
        Modules.Update(context);
        Modules.PostUpdate(context);
    }

    protected virtual void Render()
    {
        if (RenderContext == null)
        {
            return;
        }

        Modules.Render(RenderContext);
    }

    protected virtual void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        Modules.OnChatMessage(type, timestamp, sender, message, isHandled);
    }

    protected virtual void OnTerritoryChanged(ushort id)
    {
        Modules.OnTerritoryChanged(id);
    }

    private void PreRender()
    {
        frameDrawList = null;
        overlayFrameOpen = false;

        // 延後模式而且看起來可以畫：先不建繪製清單。其餘情況照原本的做法問 Pictomancy，以它的回答為準。
        if (overlayMode != OverlayMode.Deferred || !OverlayGateLooksOpen())
        {
            var draw = PictoService.Draw();
            if (draw == null)
            {
                RenderContext = null;
                return;
            }

            frameDrawList = draw;
            if (overlayMode == OverlayMode.Deferred)
            {
                DisableDeferredOverlay("it drew while the gate looked closed");
            }
            else if (overlayMode == OverlayMode.Probing)
            {
                overlayMode = OverlayMode.Deferred;
            }
        }

        overlayFrameOpen = true;
        RenderContext = sharedRenderContext ??= new RenderContext(this);
    }

    /// <summary>本幀的世界疊加層繪製清單；第一次有人要時才向 Pictomancy 建立。只能在本插件的 Draw 回呼之間呼叫，否則擲 InvalidOperationException。</summary>
    internal PctDrawList OverlayDrawList()
    {
        if (frameDrawList != null)
        {
            return frameDrawList;
        }

        if (!overlayFrameOpen)
        {
            throw new InvalidOperationException("OverlayDrawList called outside of this plugin's Draw callbacks");
        }

        var draw = PictoService.Draw();
        if (draw == null)
        {
            DisableDeferredOverlay("it refused to draw while the gate looked open");
            throw new InvalidOperationException("Pictomancy refused to draw this frame");
        }

        return frameDrawList = draw;
    }

    // 與 PictoService.Draw() 回 null 的三個執行期條件相同（過場、畫面淡出、繪製裝置暫時取不到）。
    // 只當提示用：看起來關著時仍以 Draw() 的結果為準，看起來開著卻建不出來就停用延後。
    private static unsafe bool OverlayGateLooksOpen()
    {
        if (Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Svc.Condition[ConditionFlag.WatchingCutscene78])
        {
            return false;
        }

        if (IsAddonVisible("FadeMiddle") || IsAddonVisible("FadeBlack"))
        {
            return false;
        }

        return Device.Instance() != null;
    }

    private static unsafe bool IsAddonVisible(string name)
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible;
    }

    private void DisableDeferredOverlay(string why)
    {
        overlayMode = OverlayMode.Disabled;
        Logger.Warning("World overlay: Pictomancy and the deferred-draw gate disagreed ({Why:l}); creating the draw list every frame from now on", why);
    }

    private void PostRender()
    {
        overlayFrameOpen = false;

        // 這一幀沒有人畫世界疊加層：不跑那一輪，也不貼一張全透明的貼圖。
        if (RenderContext == null || frameDrawList == null)
        {
            return;
        }

        frameDrawList = null;

        try
        {
            PictoService.GetDrawList().Dispose();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnPluginListChanged()
    {
        if (OcelotFeature.IPC.IsEnabled())
        {
            IPC.Initialize();
        }

        Modules.InjectIPCs();
    }

    public virtual void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= PostRender;
        Svc.PluginInterface.UiBuilder.Draw -= Render;
        Svc.PluginInterface.UiBuilder.Draw -= PreRender;

        PluginWatcher.OnPluginListChanged -= OnPluginListChanged;

        Modules.Dispose();
        Windows.Dispose();
        IPC.Dispose();
        Commands.Dispose();
        ChainManager.Close();

        Svc.Framework.Update -= Update;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

        PictoService.Dispose();
        ECommonsMain.Dispose();
    }
}
