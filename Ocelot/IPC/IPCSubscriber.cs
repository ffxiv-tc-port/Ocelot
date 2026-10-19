using System;
using System.Collections;
using System.Reflection;
using ECommons.EzIpcManager;
using ECommons.Reflection;

namespace Ocelot.IPC;

public abstract class IPCSubscriber
{
    protected readonly string Identifier;

    // 上次確認已載入的那個插件條目（Dalamud 的 LocalPlugin），只當捷徑：每次都讀它現在的 instance，
    // 是 null（卸載、更新、重新載入）就照原本的方式走訪整份清單，所以沒有「回舊答案」的時間窗。
    // 前提：instance 有值的條目一定還在已安裝清單上（Dalamud 卸載時先清 instance，之後才可能移出清單）。
    // 可能從框架執行緒以外呼叫：狀態只有這兩個參考欄位，先寫 instanceField 再寫 loadedEntry，搶輸的一方只是多走訪一次。
    private object? loadedEntry;

    private FieldInfo? instanceField;

    protected IPCSubscriber(string identifier)
    {
        Identifier = identifier;
        EzIPC.Init(this, identifier);
    }

    public bool IsReady()
    {
        var entry = loadedEntry;
        if (entry != null && instanceField!.GetValue(entry) != null)
        {
            return true;
        }

        loadedEntry = null;
        if (!DalamudReflector.TryGetDalamudPlugin(Identifier, out var instance, false, true))
        {
            return false;
        }

        RememberEntry(instance);
        return true;
    }

    // 找出這個 instance 屬於清單上哪個條目。找不到或反射失敗就不記：之後每次照舊整份走訪，答案不受影響。
    private void RememberEntry(object instance)
    {
        try
        {
            var manager = DalamudReflector.GetPluginManager();
            var installed = (IList)manager.GetType().GetProperty("InstalledPlugins")!.GetValue(manager)!;
            foreach (var entry in installed)
            {
                // instance 是 LocalPlugin 的私有欄位：LocalDevPlugin 是衍生類別，要從基底型別拿。
                var type = entry.GetType().Name == "LocalDevPlugin" ? entry.GetType().BaseType! : entry.GetType();
                var field = type.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && ReferenceEquals(field.GetValue(entry), instance))
                {
                    instanceField = field;
                    loadedEntry = entry;
                    return;
                }
            }
        }
        catch (Exception)
        {
            loadedEntry = null;
        }
    }
}
