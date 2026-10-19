using System;
using System.Collections.Generic;
using System.Linq;
using Ocelot.Modules;

namespace Ocelot.IPC;

public class IPCManager : IDisposable
{
    private readonly List<IPCSubscriber> subscribers = [];

    public IReadOnlyList<IPCSubscriber> Subscribers
    {
        get => subscribers;
    }

    private readonly List<object> providers = [];

    public IReadOnlyList<object> Providers
    {
        get => providers;
    }

    // 插件清單每次變動都會再呼叫：已登記過的型別沿用原本那一個（GetSubscriber 一向回第一個，模組與動作鏈拿著的也是它），只補還沒有的。
    // 刻意不清掉重建：訂閱者只有呼叫端的 EzIPC 委派，沒有要釋放的註冊；換新只會讓舊參考與新注入的分成兩份。
    public void Initialize()
    {
        foreach (var type in Registry.GetTypesImplementing<IPCSubscriber>())
        {
            if (subscribers.Any(s => s.GetType() == type))
            {
                continue;
            }

            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null || Activator.CreateInstance(type) is not IPCSubscriber instance)
            {
                continue;
            }

            Logger.Info($"Registered IPCSubscriber: {type.FullName}");
            subscribers.Add(instance);
        }

        foreach (var type in Registry.GetTypesWithAttribute<OcelotIPCAttribute>())
        {
            if (providers.Any(p => p.GetType() == type))
            {
                continue;
            }

            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null || Activator.CreateInstance(type) is not { } instance)
            {
                continue;
            }

            Logger.Info($"Registered OcelotIPC: {type.FullName}");
            providers.Add(instance);
        }
    }

    public void AddProvider(object provider)
    {
        providers.Add(provider);
    }

    public IPCSubscriber GetSubscriber(Type type)
    {
        var subscriber = subscribers.FirstOrDefault(type.IsInstanceOfType);
        if (subscriber == null)
        {
            throw new UnableToLoadIpcSubscriberException($"IPC subscriber of type {type.Name} was not found.");
        }

        // if (!subscriber.IsReady())
        // {
        //     throw new UnableToLoadIpcSubscriberException($"IPC subscriber of type {type.Name} was not ready.");
        // }

        return subscriber;
    }

    public T GetSubscriber<T>() where T : IPCSubscriber
    {
        // 主視窗每一列每幀都問好幾次：直接走訪清單，不建 LINQ 迭代器。回第一個該型別的（同 OfType<T>().FirstOrDefault()）。
        T? subscriber = null;
        foreach (var candidate in subscribers)
        {
            if (candidate is T typed)
            {
                subscriber = typed;
                break;
            }
        }

        if (subscriber == null)
        {
            throw new UnableToLoadIpcSubscriberException($"IPC subscriber of type {typeof(T).Name} was not found.");
        }

        // if (!subscriber.IsReady())
        // {
        //     throw new UnableToLoadIpcSubscriberException($"IPC subscriber of type {typeof(T).Name} was not ready.");
        // }

        return subscriber;
    }

    public bool TryGetSubscriber<T>(out T? subscriber) where T : IPCSubscriber
    {
        try
        {
            subscriber = GetSubscriber<T>();
            return subscriber.IsReady();
        }
        catch (UnableToLoadIpcSubscriberException ex)
        {
            Logger.Error(ex.Message);
            subscriber = null;
            return false;
        }
    }

    public void Dispose()
    {
        providers.Clear();
        subscribers.Clear();
    }
}
