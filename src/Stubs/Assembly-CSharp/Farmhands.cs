namespace Logic.Farm.Buildings
{
    public class BaseBuilding : Il2CppSystem.Object
    {
        public BaseBuilding(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public virtual uint Id =>
            throw new NotSupportedException("Compile-time reference only.");

        public virtual void Tick(uint ticksPassed) =>
            throw new NotSupportedException("Compile-time reference only.");

        public virtual void Removed() =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class Building : BaseBuilding
    {
        public Building(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class RangeBuilding : Building
    {
        public RangeBuilding(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public virtual Milkstone.Utils.Int2 Range =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class FarmhandBuilding : RangeBuilding
    {
        public FarmhandBuilding(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public override void Tick(uint ticksPassed) =>
            throw new NotSupportedException("Compile-time reference only.");

        public uint MaxEnergy =>
            throw new NotSupportedException("Compile-time reference only.");

        public override Milkstone.Utils.Int2 Range =>
            throw new NotSupportedException("Compile-time reference only.");

        public uint IncreaseEnergy(uint amount) =>
            throw new NotSupportedException("Compile-time reference only.");

        public override void Removed() =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

namespace View.Farmhands
{
    public class FarmhandView : UnityEngine.MonoBehaviour
    {
        public FarmhandView(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public void StartWorking(Logic.FarmTileId tileId) =>
            throw new NotSupportedException("Compile-time reference only.");
    }

    public class LocalFarmhandView : FarmhandView
    {
        public LocalFarmhandView(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public void UpdateGoingToTile(float deltaTime) =>
            throw new NotSupportedException("Compile-time reference only.");

        public void UpdateIdle() =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}
