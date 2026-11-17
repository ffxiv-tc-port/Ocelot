using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ocelot.Modules;
using Ocelot.States;

namespace Ocelot;

public static class Registry
{
    private readonly static HashSet<Assembly> RegisteredAssemblies = new();

    private readonly static List<Type> CachedTypes = new();

    public static void RegisterAssemblies(params Assembly[] assemblies)
    {
        foreach (var asm in assemblies)
        {
            if (RegisteredAssemblies.Add(asm))
            {
                try
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray()!;
                    }

                    foreach (var type in types.Where(t => t != null))
                    {
                        Logger.Debug($"[Registry] Registered type: {type.FullName}");
                    }

                    CachedTypes.AddRange(types.Where(t => t != null));
                }
                catch
                {
                }
            }
        }
    }

    public static IEnumerable<Type> GetAllLoadableTypes()
    {
        return CachedTypes;
    }

    public static IEnumerable<Type> GetTypesWithAttribute<TAttr>() where TAttr : Attribute
    {
        return GetAllLoadableTypes().Where(t => t.GetCustomAttribute<TAttr>() != null);
    }

    public static IEnumerable<(Type type, TAttr? attr)> GetTypesWithAttributeData<TAttr>() where TAttr : Attribute
    {
        return GetAllLoadableTypes()
            .Select(t => (type: t, attr: t.GetCustomAttribute<TAttr>()))
            .Where(t => t.attr != null);
    }

    public static IEnumerable<Type> GetTypesImplementing<TBase>()
    {
        return GetAllLoadableTypes()
            .Where(t => !t.IsAbstract && typeof(TBase).IsAssignableFrom(t));
    }

    public static IEnumerable<Type> GetTypesForStateMachine<T, M>()
        where T : struct, Enum
        where M : IModule
    {
        return GetAllLoadableTypes()
            .Where(t =>
                typeof(StateHandler<T, M>).IsAssignableFrom(t) &&
                !t.IsAbstract &&
                t.GetCustomAttribute<StateAttribute<T>>() is not null);
    }
}
