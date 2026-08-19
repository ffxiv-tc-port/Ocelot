using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Ocelot.Extensions;
using Ocelot.Ui;
using Ocelot.Windows;
using ItemData = Lumina.Excel.Sheets.Item;

namespace Ocelot.Gameplay;

public unsafe class Item(uint id)
{
    public readonly ItemData Data = Svc.Data.GetExcelSheet<ItemData>().GetRow(id);

    public int Count()
    {
        try
        {
            return InventoryManager.Instance()->GetInventoryItemCount(id);
        }
        catch
        {
            return 0;
        }
    }

    public virtual void Use()
    {
        try
        {
            // 🔴 AgentInventoryContext.Instance() 由 [Agent(AgentId.InventoryContext)] 產生:
            //    內部鏈 AgentModule -> UIModule -> Framework,任一層回 null 整條就回 null
            //    (登入前、切場景時是常態)。外圍的 try 只擋得住底層 [StaticAddress]/
            //    [MemberFunction] 特徵碼失配時擲出的 InvalidOperationException;
            //    裸解參考 null 原生指標是 AccessViolationException,在 .NET Core 屬
            //    corrupted-state exception,try/catch 完全攔不到 ⇒ 必須事前判空。
            //    fail-closed:agent 不在就不用道具(靜默,呼叫端本來就吞例外)。
            var agent = AgentInventoryContext.Instance();
            if (agent == null)
            {
                return;
            }

            agent->UseItem(id);
        }
        catch
        {
            // ignored
        }
    }

    public void Render(RenderContext context)
    {
        var value = $"{Count()}";
        if (Data.StackSize > 0 && Data.StackSize % 1000 != 999)
        {
            value += $"/{Data.StackSize}";
        }

        OcelotUi.LabelledValue(Data.Plural.ExtractText().ToTitleCase(), value);
        if (Data.StackSize > 0 && Data.StackSize % 1000 != 999)
        {
            OcelotUi.ProgressBar(Count() / (float)Data.StackSize);
        }
    }
}
