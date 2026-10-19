using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.Extensions;
using Ocelot.Gameplay;
using Ocelot.IPC;

namespace Ocelot.Prowler;

public class Prowl(Vector3 destination, IGameObject? obj = null)
{
    public ProwlState State { get; private set; } = ProwlState.NotStarted;

    public Vector3 OriginalDestination { get; private set; } = destination;

    public Vector3 FinalDestination { get; private set; } = destination;

    public Vector3 Destination
    {
        get => FinalDestination;
        set => FinalDestination = value;
    }

    // ⚠️ 不要把 IGameObject 存進欄位跨幀用：物件表回的是共用的包裝，存取時就地改寫位址，
    // 跨幀持有會靜默換人或懸空；IsValid() 只檢查「玩家有沒有登入」、完全不驗證位址。
    // 存 GameObjectId，每次讀取時重查物件表，查不到就回 null 讓呼叫端自己停。
    private ulong? gameObjectId = obj?.GameObjectId;

    public IGameObject? GameObject
    {
        get => gameObjectId is null ? null : Svc.Objects.SearchById(gameObjectId.Value);
        private set => gameObjectId = value?.GameObjectId;
    }

    public readonly Vector3 OriginalStart = Player.Position;

    public Vector3 FinalStart { get; set; } = Player.Position;

    public Vector3 Start
    {
        get => FinalStart;
        set => FinalStart = value;
    }

    public Func<Prowl, bool> ShouldFly { get; init; } = _ => true;

    public Func<Prowl, bool> ShouldMount { get; init; } = _ => !Svc.Condition[ConditionFlag.InCombat];

    public uint Mount { get; init; } = 0;

    public Func<Prowl, bool> ShouldSprint { get; init; } = _ => true;

    public Func<Prowl, bool> ShouldTrack { get; init; } = _ => false;

    public List<Vector3> OriginalNodes { get; private set; } = [];

    public List<Vector3> FinalNodes { get; private set; } = [];


    public List<Vector3> Nodes
    {
        get => FinalNodes;
        set => FinalNodes = value;
    }

    public Action<Prowl> PreProcessor { get; init; } = _ => { };

    public Action<Prowl> PostProcessor { get; init; } = _ => { };

    public Func<Prowl, bool> Watcher { get; init; } = _ => false;

    public Action<Prowl, VNavmesh> OnComplete { get; init; } = (prowl, vnavmesh) => { };

    public Action<Prowl, VNavmesh> OnCancel { get; init; } = (prowl, vnavmesh) => { };

    private Task<List<Vector3>>? pathfindingTask = null;

    // 追蹤用的重新尋路；只在動作鏈的步驟（框架執行緒）裡讀寫。鏈結束或取消時清掉，免得下一輪套到上一輪的結果。
    private Task<List<Vector3>>? trackingTask = null;

    public float EuclideanDistance
    {
        get => Vector3.Distance(Start, Destination);
    }

    public float OriginalPathLength
    {
        get => OriginalNodes.Length();
    }

    public float PathLength
    {
        get => Nodes.Length();
    }

    private static ChainQueue ChainQueue
    {
        get => ChainManager.Get("Prolwer.Prowl.ChainQueue");
    }

    public Prowl(IGameObject gameObject)
        : this(gameObject.Position)
    {
        GameObject = gameObject;
    }

    public Func<Chain.Chain> GetChain(VNavmesh vnavmesh)
    {
        return () => Chain.Chain.Create($"Prowl({Start:f2}, {Destination:f2})")
            .Debug("Waiting for vnavmesh not to be running")
            .Then(_ => !vnavmesh.IsRunning())
            .Debug("Running preprocessor")
            .Then(_ => PreProcessor.Invoke(this))
            .Debug("Starting pathfinding task")
            .Then(_ => pathfindingTask = vnavmesh.Pathfind(Start, Destination, ShouldFly(this)))
            .Then(_ => State = ProwlState.Pathfinding)
            .BreakIf(() => pathfindingTask == null)
            .Debug("Waiting for pathfinding to be done")
            .Then(_ => !vnavmesh.IsRunning() && pathfindingTask!.IsCompleted)
            .Then(_ =>
            {
                if (pathfindingTask!.IsCanceled)
                {
                    Logger.Debug("Pathfinding task cancelled");
                }

                if (pathfindingTask.IsFaulted)
                {
                    Logger.Debug("Pathfinding task faulted");
                }
            })
            .BreakIf(() => pathfindingTask!.IsCanceled || pathfindingTask!.IsFaulted)
            .Debug("Getting pathfinding result")
            .Then(_ =>
            {
                OriginalNodes = new List<Vector3>(pathfindingTask!.Result);
                Nodes = new List<Vector3>(pathfindingTask!.Result);
            })
            .Debug("Running postprocessor")
            .Then(_ => PostProcessor.Invoke(this))
            .ConditionalThen(_ => ShouldMount(this) && !Player.Mounted, _ =>
            {
                State = ProwlState.Mounting;

                if (Mount == 0)
                {
                    Actions.MountRoulette.Cast();
                }
                else
                {
                    Actions.Mount(Mount).Cast();
                }
            })
            .Then(_ =>
            {
                if (!ShouldMount(this) || Player.Mounted)
                {
                    return true;
                }

                if (!Player.IsCasting || !Player.Mounting)
                {
                    throw new Exception("Failed to mount");
                }

                return false;
            })
            .ConditionalThen(_ => !ShouldFly(this) && ShouldSprint(this) && Actions.Sprint.CanCast() && !Player.Mounted, _ => Actions.Sprint.Cast())
            .Debug("Following Path")
            .Then(_ => vnavmesh.MoveTo(Nodes, ShouldFly(this)))
            .Then(_ => State = ProwlState.Moving)
            .Then(_ =>
            {
                if (!vnavmesh.IsRunning() || Watcher.Invoke(this))
                {
                    trackingTask = null;
                    return true;
                }

                // 追蹤用的重新尋路整段留在這條（框架）執行緒上：這一步每幀被問一次，尋路結果用輪詢接，
                // 不開背景工作——讀玩家座標與呼叫 vnavmesh 的 IPC 都不可以在執行緒池上做。
                // 追蹤失敗只是沿用原路徑，不可以讓例外中止整條動作鏈，所以兩段都自己接。
                if (trackingTask != null)
                {
                    if (!trackingTask.IsCompleted)
                    {
                        return false;
                    }

                    var finished = trackingTask;
                    trackingTask = null;
                    try
                    {
                        var nodes = finished.GetAwaiter().GetResult().Smooth().ContinueFrom(Player.Position);
                        vnavmesh.Stop();
                        vnavmesh.MoveTo(nodes, false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Prowl 追蹤換路徑失敗，沿用原路徑：{ex.GetType().Name}：{ex.Message}");
                    }

                    return false;
                }

                if (!ShouldTrack(this))
                {
                    return false;
                }

                // 每幀重查物件表；查不到代表追蹤目標已消失，直接放棄追蹤。
                var tracked = GameObject;
                if (tracked == null)
                {
                    return false;
                }

                if (tracked.Position.DistanceTo2D(Destination) <= tracked.HitboxRadius)
                {
                    return false;
                }

                try
                {
                    trackingTask = vnavmesh.Pathfind(Player.Position, tracked.GetPointOnHitboxFromPlayer(2f), false);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Prowl 追蹤尋路送不出去，沿用原路徑：{ex.GetType().Name}：{ex.Message}");
                }

                return false;
            })
            .Then(_ => vnavmesh.Stop())
            .Then(_ =>
            {
                OnComplete.Invoke(this, vnavmesh);
                State = ProwlState.Complete;
            })
            .OnCancel(() =>
            {
                // 鏈裡那個 vnavmesh.Stop() 步驟只在「正常跑完」時會執行；取消／逾時／例外都是清空佇列，
                // 不在這裡補一次，角色會照著上一條路徑繼續跑。
                // 放 OnCancel 而不是 OnFinally：OnFinally 連正常完成也會跑，那條路徑已經停過一次。
                // 包 try/catch 的理由：Stop 是 vnavmesh 的 IPC，對方沒載入／正在卸載時會擲例外；這段排在佇列裡跑時，
                // 例外會讓 ECommons 再 Abort() 一次，把排在後面的 OnFinally 一起清掉。
                try
                {
                    vnavmesh.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Prowl 取消收尾時停不下 vnavmesh：{ex.GetType().Name}：{ex.Message}");
                }

                trackingTask = null;
                OnCancel(this, vnavmesh);
                State = ProwlState.Cancelled;
            });
    }

    public void Redirect(Vector3 destination)
    {
        Redirect(destination, null);
    }

    public void Redirect(IGameObject obj)
    {
        Redirect(obj.Position, obj);
    }

    /// <remarks>
    /// 🔴 這段的正確性完全靠「<see cref="Prowler.Abort"/> 是同步的」：
    /// <c>Chain.Abort()</c> 會就地把子動作鏈一起中止、就地跑完取消收尾，才回到這裡。
    /// 所以上一輪對<b>同一個</b> <see cref="Prowl"/> 物件做的所有寫入
    /// （<c>State = Cancelled</c>、呼叫端掛的 <see cref="OnCancel"/>）
    /// 全部發生在本方法設定新一輪的狀態之前，蓋不到新一輪的狀態。
    /// </remarks>
    private void Redirect(Vector3 destination, IGameObject? obj)
    {
        Prowler.Abort();
        Reset();

        State = ProwlState.Redirecting;
        OriginalDestination = destination;
        Destination = destination;
        Start = Player.Position;
        GameObject = obj;

        Prowler.Prowl(this);
    }

    public void Reset()
    {
        State = ProwlState.NotStarted;
        Start = OriginalStart;
        Destination = OriginalDestination;
        OriginalNodes.Clear();
        FinalNodes.Clear();
    }
}
