using System;
using System.Collections.Generic;
using System.Numerics;
using ECommons.EzIpcManager;

namespace Ocelot.IPC;

#pragma warning disable CS8618
public class Lifestream : IPCSubscriber
{
    public Lifestream() : base("Lifestream")
    {
    }

    // [EzIPC] public readonly Func<string, string, string, string, bool, bool, AddressBookEntryTuple> BuildAddressBookEntry = null!;

    // [EzIPC] public readonly Func<AddressBookEntryTuple, bool> IsHere = null!;

    // [EzIPC] public readonly Func<AddressBookEntryTuple, bool> IsQuickTravelAvailable = null!;

    // [EzIPC] public readonly Action<AddressBookEntryTuple> GoToHousingAddress = null!;

    [EzIPC] public readonly Func<bool> IsBusy = null!;

    [EzIPC] public readonly Action Abort = null!;

    [EzIPC] public readonly Func<string, bool> CanVisitSameDC = null!;

    [EzIPC] public readonly Func<string, bool> CanVisitCrossDC = null!;

    [EzIPC] public readonly Action<string, bool, string, bool, int?, bool?, bool?> TPAndChangeWorld = null!;

    [EzIPC] public readonly Func<uint, int?> GetWorldChangeAetheryteByTerritoryType = null!;

    [EzIPC] public readonly Func<string, bool> ChangeWorld = null!;

    [EzIPC] public readonly Func<uint, bool> ChangeWorldById = null!;

    [EzIPC] public readonly Func<string, bool> AethernetTeleport = null!;

    [EzIPC] public readonly Func<uint, bool> AethernetTeleportByPlaceNameId = null!;

    [EzIPC] public readonly Func<uint, bool> AethernetTeleportById = null!;

    [EzIPC] public readonly Func<uint, bool> HousingAethernetTeleportById = null!;

    [EzIPC] public readonly Func<bool> AethernetTeleportToFirmament = null!;

    [EzIPC] public readonly Func<uint> GetActiveAetheryte = null!;

    [EzIPC] public readonly Func<uint> GetActiveCustomAetheryte = null!;

    [EzIPC] public readonly Func<uint> GetActiveResidentialAetheryte = null!;

    [EzIPC] public readonly Func<uint, byte, bool> Teleport = null!;

    [EzIPC] public readonly Func<bool> TeleportToFC = null!;

    [EzIPC] public readonly Func<bool> TeleportToHome = null!;

    [EzIPC] public readonly Func<bool> TeleportToApartment = null!;

    // [EzIPC] public readonly Func<ulong, (HousePathData Private, HousePathData FC)> GetHousePathData = null!;

    // [EzIPC] public readonly Func<ResidentialAetheryteKind, uint> GetResidentialTerritory = null!;

    [EzIPC] public readonly Func<uint, int, Vector3?> GetPlotEntrance = null!;

    // [EzIPC] public readonly Action<TaskPropertyShortcut.PropertyType, HouseEnterMode?> EnqueuePropertyShortcut = null!;

    [EzIPC] public readonly Action<bool> EnterApartment = null!;

    [EzIPC] public readonly Action<int?> EnqueueInnShortcut = null!;

    [EzIPC] public readonly Action<int?> EnqueueLocalInnShortcut = null!;

    // [EzIPC] public readonly Func<(ResidentialAetheryteKind Kind, int Ward, int Plot)?> GetCurrentPlotInfo = null!;

    [EzIPC] public readonly Func<bool> CanChangeInstance = null!;

    [EzIPC] public readonly Func<int> GetNumberOfInstances = null!;

    [EzIPC] public readonly Action<int> ChangeInstance = null!;

    [EzIPC] public readonly Func<int> GetCurrentInstance = null!;

    [EzIPC] public readonly Func<bool?> HasApartment = null!;

    [EzIPC] public readonly Func<bool?> HasPrivateHouse = null!;

    [EzIPC] public readonly Func<bool?> HasFreeCompanyHouse = null!;

    [EzIPC] public readonly Action<List<Vector3>> Move = null!;

    [EzIPC] public readonly Func<bool> CanMoveToWorkshop = null!;

    [EzIPC] public readonly Action MoveToWorkshop = null!;

    [EzIPC] public readonly Func<uint> GetRealTerritoryType = null!;

    [EzIPCEvent] public readonly Action OnHouseEnterError = null!;
}
