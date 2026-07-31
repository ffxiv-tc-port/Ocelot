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

    // ⚠️ 不要把 IGameObject 存進欄位跨幀用。
    // Dalamud 的 GameObject.Address 在建構時就凍結、永不重新解析
    // (GameObject.cs:137-139,所有屬性都走 Struct => (GameObject*)this.Address),
    // 而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、完全不驗證位址
    // (GameObject.cs:170-177)。所以存 IGameObject == 存一根原生指標。
    // 原本 GameObject 是建構時凍結的參考,而下面的追蹤步驟每幀重跑、
    // 甚至在 Task.Run 的背景執行緒上解參考 → 目標消失後就是攔不到的 AccessViolation。
    // 正解:存 GameObjectId,每次讀取時重查物件表,查不到就回 null 讓呼叫端自己停。
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

    private Task? trackingTask = null;

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
                    return true;
                }

                if (!ShouldTrack(this) || trackingTask is { IsCompleted: false })
                {
                    return false;
                }

                // 每幀重查物件表;查不到代表追蹤目標已消失,直接放棄追蹤(fail-closed),
                // 而不是繼續拿建構時凍結的位址去解參考。
                var tracked = GameObject;
                if (tracked == null)
                {
                    return false;
                }

                if (trackingTask is { IsCompleted: true })
                {
                    trackingTask.Dispose();
                    trackingTask = null;
                }

                if (tracked.Position.DistanceTo2D(Destination) <= tracked.HitboxRadius)
                {
                    return false;
                }

                // 在主執行緒上把座標算完再丟進背景工作。原本是把 IGameObject 捕獲進
                // Task.Run,再於背景執行緒上解參考它的 Position/HitboxRadius——
                // 那是跨幀「又」跨執行緒的懸空指標。
                var trackPoint = tracked.GetPointOnHitboxFromPlayer(2f);

                trackingTask = Task.Run(async () =>
                {
                    var nodes = await vnavmesh.Pathfind(Player.Position, trackPoint, false);
                    nodes = nodes.Smooth().ContinueFrom(Player.Position);
                    vnavmesh.Stop();
                    vnavmesh.MoveTo(nodes, false);
                });

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
