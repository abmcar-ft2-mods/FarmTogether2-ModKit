namespace Logic.Town
{
    public class TownData : Il2CppSystem.Object
    {
        public TownData(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Il2CppSystem.Collections.Generic.List<TownSlot> Slots =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TownItemInstance : Il2CppSystem.Object
    {
        public TownItemInstance(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TownSlot : Il2CppSystem.Object
    {
        public TownSlot(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public TownItemInstance Contents =>
            throw new NotSupportedException("Compile-time reference only.");

        public bool IsEmpty =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

namespace Logic.Town.Items
{
    public class TownUpgradeableInstance : Logic.Town.TownItemInstance
    {
        public TownUpgradeableInstance(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class TownShopInstance : TownUpgradeableInstance
    {
        public TownShopInstance(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.Definition.Town.TownShopDefinition Definition =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.FarmMoney GetSellMoney_Resource(int resourceIndex) =>
            throw new NotSupportedException("Compile-time reference only.");

        public uint GetRemainingUses(int resourceIndex) =>
            throw new NotSupportedException("Compile-time reference only.");

        public void SellResources(LocalPlayer player, int resourceIndex, uint interactionCount) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}
