using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ECommons.DalamudServices;
using Lumina.Excel;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace Ocelot.Config.Handlers;

public class ExcelSheetHandler<T> : SelectHandler<T>
    where T : struct, IExcelRow<T>
{
    protected override Type type
    {
        get => typeof(uint);
    }

    private readonly IExcelSheetItemProvider<T> provider;

    public ExcelSheetHandler(ModuleConfig self, ConfigAttribute attribute, PropertyInfo prop, string provider)
        : base(self, attribute, prop)
    {
        if (!string.IsNullOrEmpty(self.ProviderNamespace))
        {
            provider = $"{self.ProviderNamespace}.{provider}";
        }

        // Use your Registry to find the type
        var providerType = Registry.GetAllLoadableTypes()
            .FirstOrDefault(t => t.FullName == provider);

        if (providerType == null)
        {
            throw new InvalidOperationException($"Provider type '{provider}' not found.");
        }

        var expectedInterface = typeof(IExcelSheetItemProvider<>).MakeGenericType(typeof(T));

        if (!expectedInterface.IsAssignableFrom(providerType))
        {
            // Provide more helpful debugging info
            var implementedInterfaces = providerType.GetInterfaces();
            var matchingInterfaces = implementedInterfaces
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IExcelSheetItemProvider<>))
                .ToList();

            var debugInfo = string.Join("\n", matchingInterfaces.Select(i => $"- {i.FullName}"));
            throw new InvalidOperationException(
                $"Provider type '{providerType.FullName}' does not implement IExcelSheetItemProvider<{typeof(T).Name}>.\n" +
                $"Found generic interfaces:\n{debugInfo}"
            );
        }

        try
        {
            this.provider = (IExcelSheetItemProvider<T>)Activator.CreateInstance(providerType)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create instance of provider '{providerType.FullName}': {ex.Message}", ex);
        }
    }

    protected override T GetValue(RenderContext context)
    {
        var value = (uint)(context.GetValue() ?? 1);
        var sheet = Svc.Data.GetExcelSheet<T>();

        // 這個方法每幀都會被下拉選單呼叫，原本的 First(d => d.RowId == value) 是整表線性掃描。
        // 改用 O(1) 的 GetRowOrDefault；查不到時回退表格第一列，而不是丟例外中斷整個設定視窗的 Draw。
        return sheet.GetRowOrDefault(value) ?? sheet.FirstOrDefault();
    }

    protected override void SetValue(RenderContext context, T value)
    {
        context.SetValue(value.RowId);
    }

    protected override IEnumerable<T> GetData()
    {
        return Svc.Data.GetExcelSheet<T>().Where(provider.Filter);
    }


    protected override bool Filter(T item)
    {
        return provider.Filter(item);
    }

    protected override string GetLabel(T item)
    {
        return provider.GetLabel(item);
    }
}
