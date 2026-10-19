using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using Ocelot.Chain;

namespace Ocelot.Debug;

public class DebugIPCProvider
{
    private readonly OcelotPlugin plugin;

    public DebugIPCProvider(OcelotPlugin plugin)
    {
        this.plugin = plugin;

        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.Debug");
    }

    [EzIPC]
    private string GetOcelotVersion()
    {
        return OcelotPlugin.OcelotVersion;
    }

    [EzIPC]
    private string GetPluginVersion()
    {
        return plugin.Version;
    }

    [EzIPC]
    private string GetPluginName()
    {
        return plugin.Name;
    }

    // 佇列表要在鎖內拷出來：與 framework 執行緒的增刪並行走訪會擲例外。其餘只讀計數與名稱。
    [EzIPC]
    private Dictionary<string, QueueInfo> GetChainQueueInfo()
    {
        return ChainManager.SnapshotQueues().ToDictionary(p => p.Key, p => QueueInfo.FromChainQueue(p.Value));
    }

    /// <remarks>
    /// IPC 跑在呼叫端執行緒，中止動作鏈會碰 ECommons <c>TaskManager</c>（不是執行緒安全的）：
    /// framework 執行緒上當場中止（與原本相同）；其他執行緒排到下一幀、不等。
    /// 框架卸載中不排（工作會直接被取消），插件卸載時 <c>ChainManager.Close</c> 本來就會收掉全部動作鏈。
    /// </remarks>
    [EzIPC]
    private void AbortAllChains()
    {
        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            ChainManager.AbortAll();
            return;
        }

        if (Svc.Framework.IsFrameworkUnloading)
        {
            return;
        }

        Svc.Framework.Run(AbortAllOnFramework);
    }

    private static void AbortAllOnFramework()
    {
        try
        {
            ChainManager.AbortAll();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[Ocelot] AbortAllChains failed on the framework thread");
        }
    }
}
