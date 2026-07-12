namespace Logic
{
    public enum Border
    {
        Top = 0,
        Right = 1,
        Bottom = 2,
        Left = 3
    }

    public enum FarmFeatureLevel
    {
    }

    public enum FarmFloorType : byte
    {
    }

    public enum FarmResourceType : byte
    {
        Count = 52
    }

    public enum FarmTileState : byte
    {
        Empty = 0,
        Plow = 1,
        Gravel = 2,
        Building = 3,
        Flower = 4,
        Animal = 5,
        Water = 6,
        Tree = 7,
        Road = 8
    }

    public enum PlayerPermissions
    {
        Full = 6
    }

    [Flags]
    public enum WorkFlags
    {
        None = 0,
        UsingVehicle = 1,
        TileModPriority = 2,
        IsAutoTractor = 4
    }

    public enum WorkType
    {
        Plow = 0,
        Place = 1,
        Refill = 2,
        Harvest = 3,
        Recycle = 4,
        Harvest_Replant = 12
    }

    public class ItemDefinition : UnityEngine.ScriptableObject
    {
        public ItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

namespace Logic.Definition
{
    public class ShopItemDefinition : Logic.ItemDefinition
    {
        public ShopItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FarmItemDefinition : ShopItemDefinition
    {
        public FarmItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class HarvestItemDefinition : FarmItemDefinition
    {
        public HarvestItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class CropDefinition : HarvestItemDefinition
    {
        public CropDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FlowerDefinition : HarvestItemDefinition
    {
        public FlowerDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TreeDefinition : HarvestItemDefinition
    {
        public TreeDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

namespace Logic.Definition.Town
{
    public class TownItemDefinition : Logic.Definition.ShopItemDefinition
    {
        public TownItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TownUpgradeableItemDefinition : TownItemDefinition
    {
        public TownUpgradeableItemDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TownShopDefinition : TownUpgradeableItemDefinition
    {
        public TownShopDefinition(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Il2CppSystem.Collections.Generic.List<TownShopResource> ShopResources =>
            throw new NotSupportedException("Compile-time reference only.");

        public class TownShopResource : Il2CppSystem.Object
        {
            public TownShopResource(IntPtr pointer) : base(pointer) =>
                throw new NotSupportedException("Compile-time reference only.");

            public Logic.FarmResource Resource =>
                throw new NotSupportedException("Compile-time reference only.");

            public int GoodIndex =>
                throw new NotSupportedException("Compile-time reference only.");
        }
    }
}
