using System;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Ocelot.Chain.ChainEx;

public static class ChainAddon
{
    /// <summary>
    /// 等待介面的任務逾時時，先把「在等哪一個介面」寫進 log，再讓 ECommons 照原本的流程中止佇列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由來：<c>WaitForAddonReady</c> / <c>WaitForAddonNotReady</c> 設了 <c>TimeoutSilently = true</c>，
    /// 那會把 ECommons 的逾時 Warning 導去 <c>Verbose</c> —— Verbose 是使用者的 log 等級唯一收不到的一級，
    /// 而呼叫點只有事前那一行 <c>chain.Debug("Waiting for addon…")</c>
    /// ⇒ 等不到介面、整條動作鏈被中止的那一刻，log 上一個字都沒有。
    /// </para>
    /// <para>
    /// 🔴 等級刻意維持 <c>Warning</c>：ECommons 原本就是 Warning，降級只會弱化訊號。
    /// 🔴 <c>TimeoutSilently</c> 也刻意不改回 <c>false</c>：ECommons 擲的 <c>TaskTimeoutException</c>
    /// <b>不帶任務名</b>，改回去只會多印一行沒有資訊的 Warning。要的是有名字的那一則。
    /// 🔴 也刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那一份。
    /// ⚠️ <paramref name="remainingTimeMS"/> 是 <c>ref</c>：改它等於偷偷延長逾時，這裡只讀不寫。
    /// </para>
    /// </remarks>
    private static void OnAddonWaitTimeout(TaskManagerTask task, ref long remainingTimeMS)
    {
        var limit = task.Configuration?.TimeLimitMS;
        Logger.Warning($"任務逾時：[{task.Name}@{task.Location}] 上限 {(limit?.ToString() ?? "?")} ms，整條動作鏈會被中止。");
    }

    private static unsafe TaskManagerTask AddonCallback(string addonName, bool updateState = true, params object[] callbackValues)
    {
        return new TaskManagerTask(() =>
        {
            if (EzThrottler.Throttle($"ChainAddon.AddonCallback({addonName}, {updateState}, {string.Join(", ", callbackValues)})"))
            {
                var addonPtr = Svc.GameGui.GetAddonByName(addonName);
                if (addonPtr != IntPtr.Zero)
                {
                    var addon = (AtkUnitBase*)addonPtr.Address;
                    if (addon->IsReady)
                    {
                        Callback.Fire(addon, updateState, callbackValues);
                        return true;
                    }
                }
            }

            return false;
        }, new TaskManagerConfiguration { TimeLimitMS = 3000 });
    }

    public static Chain AddonCallback(this Chain chain, string addonName, bool updateState = true, params object[] callbackValues)
    {
        return chain
            .Debug($"Waiting for addon callback to fire {addonName} {updateState} {string.Join(", ", callbackValues)}")
            .Then(AddonCallback(addonName, updateState, callbackValues));
    }

    private static unsafe TaskManagerTask WaitForAddonReady(string addonName, int timeout = 3000)
    {
        return new TaskManagerTask(() =>
        {
            if (EzThrottler.Throttle($"ChainAddon.WaitForAddon({addonName})"))
            {
                var addonPtr = Svc.GameGui.GetAddonByName(addonName);
                if (addonPtr != IntPtr.Zero)
                {
                    var addon = (AtkUnitBase*)addonPtr.Address;
                    return GenericHelpers.IsAddonReady(addon);
                }
            }

            return false;
        }, $"WaitForAddonReady({addonName})", new TaskManagerConfiguration { TimeLimitMS = timeout, TimeoutSilently = true, OnTaskTimeout = OnAddonWaitTimeout });
    }

    public static Chain WaitForAddonReady(this Chain chain, string addonName, int timeout = 3000)
    {
        return chain.Debug($"Waiting for addon to be ready '{addonName}'").Then(WaitForAddonReady(addonName, timeout));
    }

    private static unsafe TaskManagerTask WaitForAddonNotReady(string addonName, int timeout = 3000)
    {
        return new TaskManagerTask(() =>
        {
            if (EzThrottler.Throttle($"ChainAddon.WaitForAddon({addonName})"))
            {
                var addonPtr = Svc.GameGui.GetAddonByName(addonName);
                if (addonPtr != IntPtr.Zero)
                {
                    var addon = (AtkUnitBase*)addonPtr.Address;
                    return !GenericHelpers.IsAddonReady(addon);
                }
            }

            return false;
        }, $"WaitForAddonNotReady({addonName})", new TaskManagerConfiguration { TimeLimitMS = timeout, TimeoutSilently = true, OnTaskTimeout = OnAddonWaitTimeout });
    }

    public static Chain WaitForAddonNotReady(this Chain chain, string addonName, int timeout = 3000)
    {
        return chain.Debug($"Waiting for addon to be not ready '{addonName}'").Then(WaitForAddonNotReady(addonName, timeout));
    }
}
