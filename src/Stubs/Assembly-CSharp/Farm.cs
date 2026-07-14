namespace Logic
{
    public enum ActionPerformedType : byte
    {
        Purchase = 0,
        Harvest = 1,
        Refill = 2,
        Recycle = 3,
        Exchange = 4,
        Terraform = 5
    }

    public struct FarmTileId
    {
        public readonly short _TileX;
        public readonly short _TileY;

        public FarmTileId(short x, short y) =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool IsValid =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public struct FarmMoney
    {
        public long Coins =>
            throw new NotSupportedException("Compile-time reference only.");

        public long Bills =>
            throw new NotSupportedException("Compile-time reference only.");

        public long Medals =>
            throw new NotSupportedException("Compile-time reference only.");

        public static FarmMoney operator *(FarmMoney value, float multiplier) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public struct FarmResource
    {
        public FarmResourceType Type =>
            throw new NotSupportedException("Compile-time reference only.");

        public long Amount =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FarmResourceStorage : Il2CppSystem.Object
    {
        public FarmResourceStorage(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public long Amount =>
            throw new NotSupportedException("Compile-time reference only.");

        public long MaxValue =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public sealed class FailedAction : Il2CppSystem.ValueType
    {
        public FailedAction(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

namespace Logic.Farm
{
    public class FarmTileContents : Il2CppSystem.Object
    {
        public FarmTileContents(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FarmData : Il2CppSystem.Object
    {
        public FarmData(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public sealed class ActionPerformedHandler : Il2CppSystem.MulticastDelegate
        {
            public ActionPerformedHandler(Il2CppSystem.Object @object, IntPtr method) : base(IntPtr.Zero) =>
                throw new NotSupportedException("Compile-time reference only.");

            public ActionPerformedHandler(IntPtr pointer) : base(pointer) =>
                throw new NotSupportedException("Compile-time reference only.");

            public void Invoke(
                UnityEngine.Vector3 position,
                Logic.ActionPerformedType type,
                ulong xpIncrease,
                Logic.FarmMoney money,
                Il2CppSystem.Collections.Generic.List<Logic.FarmResource> resourceVariation,
                bool resourceDecrease) =>
                throw new NotSupportedException("Compile-time reference only.");

            public static implicit operator ActionPerformedHandler(
                Action<
                    UnityEngine.Vector3,
                    Logic.ActionPerformedType,
                    ulong,
                    Logic.FarmMoney,
                    Il2CppSystem.Collections.Generic.List<Logic.FarmResource>,
                    bool> action) =>
                throw new NotSupportedException("Compile-time reference only.");

            public static ActionPerformedHandler operator +(
                ActionPerformedHandler left,
                ActionPerformedHandler right) =>
                throw new NotSupportedException("Compile-time reference only.");

            public static ActionPerformedHandler operator -(
                ActionPerformedHandler left,
                ActionPerformedHandler right) =>
                throw new NotSupportedException("Compile-time reference only.");
        }

        public static FarmData CurrentFarm =>
            throw new NotSupportedException("Compile-time reference only.");

        public ActionPerformedHandler OnTownActionPerformed
        {
            get => throw new NotSupportedException("Compile-time reference only.");
            set => throw new NotSupportedException("Compile-time reference only.");
        }

        public FarmTile GetTile(Logic.FarmTileId tileId) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.FarmResourceStorage GetResource(Logic.FarmResourceType resourceType) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.Town.TownData TownData =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool HasFence(Logic.FarmTileId tileId, Logic.Border border) =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool IsTownShopOpen(
            Player player,
            Logic.Definition.Town.TownShopDefinition definition,
            out Logic.FailedAction failedAction) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static void ClearCurrentFarm() =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool TryPerformFarmhandWork(
            Logic.FarmTileId tileId,
            Logic.Farm.Buildings.FarmhandBuilding building) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FarmTile : Il2CppSystem.Object
    {
        public FarmTile(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.FarmFloorType FloorType =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.FarmTileId TileId =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.FarmTileState TileState =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool IsEmptyContents =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool IsVehicleCompatible(
            FarmTile origin,
            Logic.Definition.FarmItemDefinition definition,
            bool allowOverwrite,
            bool tileModPriority) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}
