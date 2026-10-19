using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ECommons.EzIpcManager;

namespace Ocelot.IPC;

public class VNavmesh() : IPCSubscriber("vnavmesh")
{
    [EzIPC("Nav.IsReady")] public Func<bool> IsNavmeshReady = null!;

    [EzIPC("Nav.BuildProgress")] public Func<float> GetBuildProgress = null!;

    [EzIPC("Nav.Reload")] public Func<bool> ReloadNavmesh = null!;

    // 回 false＝vnavmesh 節流略過了這次全量重建（不是失敗）；Nav.Reload 恆回 true。
    [EzIPC("Nav.Rebuild")] public Func<bool> RebuildNavmesh = null!;

    [EzIPC("Nav.Pathfind")] public Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind = null!;

    [EzIPC("Nav.PathfindCancelable")] public Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> PathfindCancelable = null!;

    [EzIPC("Nav.PathfindCancelAll")] public Action CancelAllPathfinds = null!;

    [EzIPC("Nav.PathfindInProgress")] public Func<bool> IsPathfinding = null!;

    [EzIPC("Nav.PathfindNumQueued")] public Func<int> NumQueuedPathfindRequests = null!;

    [EzIPC("Nav.IsAutoLoad")] public Func<bool> IsAutoLoadEnabled = null!;

    [EzIPC("Nav.SetAutoLoad")] public Action<bool> SetAutoLoad = null!;

    // 回寫出的點陣圖涵蓋範圍（最小角, 最大角）；導航網格還沒建好時提供端擲例外。
    [EzIPC("Nav.BuildBitmap")] public Func<Vector3, string, float, (Vector3, Vector3)> BuildBitmap = null!;

    [EzIPC("Nav.BuildBitmapBounded")] public Func<Vector3, string, float, Vector3, Vector3, (Vector3, Vector3)> BuildBitmapBounded = null!;

    [EzIPC("Query.Mesh.NearestPoint")] public Func<Vector3, float, float, Vector3?> FindNearestPointOnMesh = null!;

    [EzIPC("Query.Mesh.PointOnFloor")] public Func<Vector3, bool, float, Vector3?> FindPointOnFloor = null!;

    [EzIPC("Path.MoveTo")] public Action<List<Vector3>, bool> MoveTo = null!;

    [EzIPC("Path.MoveTo")] public Action<List<Vector3>, bool> FollowPath = null!;

    [EzIPC("Path.Stop")] public Action StopPath = null!;

    [EzIPC("Path.IsRunning")] public Func<bool> IsRunning = null!;

    [EzIPC("Path.NumWaypoints")] public Func<int> GetNumWaypoints = null!;

    [EzIPC("Path.ListWaypoints")] public Func<List<Vector3>> GetWaypoints = null!;

    [EzIPC("Path.GetMovementAllowed")] public Func<bool> IsMovementAllowed = null!;

    [EzIPC("Path.SetMovementAllowed")] public Action<bool> SetMovementAllowed = null!;

    [EzIPC("Path.GetAlignCamera")] public Func<bool> GetAlignCamera = null!;

    [EzIPC("Path.SetAlignCamera")] public Action<bool> SetAlignCamera = null!;

    [EzIPC("Path.GetTolerance")] public Func<float> GetTolerance = null!;

    [EzIPC("Path.SetTolerance")] public Action<float> SetTolerance = null!;

    [EzIPC("SimpleMove.PathfindAndMoveTo")]
    public Func<Vector3, bool, bool> PathfindAndMoveTo = null!;

    [EzIPC("SimpleMove.PathfindInProgress")]
    public Func<bool> IsSimpleMoveInProgress = null!;

    [EzIPC("Window.IsOpen")] public Func<bool> IsMainWindowOpen = null!;

    [EzIPC("Window.SetOpen")] public Action<bool> SetMainWindowOpen = null!;

    [EzIPC("DTR.IsShown")] public Func<bool> IsDtrShown = null!;

    [EzIPC("DTR.SetShown")] public Action<bool> SetDtrShown = null!;


    [EzIPC("Path.Stop")] public readonly Action Stop = null!;
}
