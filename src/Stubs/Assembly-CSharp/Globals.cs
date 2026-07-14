namespace Core
{
    public abstract class StageParameters : Il2CppSystem.Object
    {
        public StageParameters(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static bool IsOnline
        {
            get => throw new NotSupportedException("Compile-time reference only.");
            set => throw new NotSupportedException("Compile-time reference only.");
        }
    }
}

public class GameGlobals : Il2CppSystem.Object
{
    public GameGlobals(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public class Game : Il2CppSystem.Object
    {
        public Game(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static float TractorWorkInterval(Logic.FarmFeatureLevel featureLevel, bool antiCheat) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static float TractorWorkSpeed(Logic.FarmFeatureLevel featureLevel) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static float WorkDuration(
            Logic.WorkType workType,
            Logic.WorkFlags workFlags,
            Logic.Farm.FarmTileContents contents,
            Logic.Definition.FarmItemDefinition definition,
            bool antiCheat,
            ref float multiplier) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

public class MilkCharacterController : UnityEngine.MonoBehaviour
{
    public MilkCharacterController(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public float WalkingSpeed
    {
        get => throw new NotSupportedException("Compile-time reference only.");
        set => throw new NotSupportedException("Compile-time reference only.");
    }

    public UnityEngine.Vector3 CurrentVelocity =>
        throw new NotSupportedException("Compile-time reference only.");

    public UnityEngine.Vector3 OverrideVelocity =>
        throw new NotSupportedException("Compile-time reference only.");

    public UnityEngine.Vector3 WalkVelocity =>
        throw new NotSupportedException("Compile-time reference only.");

    public void updateMovement(ref UnityEngine.Vector3 velocity, float deltaTime) =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual void KinematicCharacterController_ICharacterController_UpdateVelocity(
        ref UnityEngine.Vector3 currentVelocity,
        float deltaTime) =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class Player : UnityEngine.MonoBehaviour
{
    public Player(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public Logic.PlayerPermissions Permissions =>
        throw new NotSupportedException("Compile-time reference only.");

    public MilkCharacterController Controller =>
        throw new NotSupportedException("Compile-time reference only.");

    public PlayerState State =>
        throw new NotSupportedException("Compile-time reference only.");

    public WorkStateInfo WorkState =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual bool IsRemote =>
        throw new NotSupportedException("Compile-time reference only.");

    public static bool SkipWorkThrottle
    {
        get => throw new NotSupportedException("Compile-time reference only.");
        set => throw new NotSupportedException("Compile-time reference only.");
    }

    public bool UsingVehicle =>
        throw new NotSupportedException("Compile-time reference only.");

    public bool CheckWorkThrottle(
        Logic.WorkFlags workFlags,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles) =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual void Update() =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual void updateCharacterControllerParameters() =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual bool DidPerformWork(
        Logic.WorkType workType,
        Logic.Definition.FarmItemDefinition definition,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles,
        int selectedIndex,
        Logic.WorkFlags workFlags,
        double timestamp) =>
        throw new NotSupportedException("Compile-time reference only.");

    public enum PlayerState
    {
        Idle = 0,
        Working = 1,
        WorkGestureStart = 8,
        WorkGesture = 9,
        WorkGestureEnd = 10,
        AutoTractor = 12
    }

    public class WorkStateInfo : Il2CppSystem.Object
    {
        public WorkStateInfo(IntPtr pointer) : base(pointer) =>
            throw new NotSupportedException("Compile-time reference only.");

        public Logic.Definition.FarmItemDefinition WorkItemDefinition =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}

public sealed class LocalPlayer : Player
{
    public LocalPlayer(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> ContextualTiles =>
        throw new NotSupportedException("Compile-time reference only.");

    public Logic.Farm.FarmTile CurrentTile =>
        throw new NotSupportedException("Compile-time reference only.");

    public Logic.FarmTileId CurrentTileId =>
        throw new NotSupportedException("Compile-time reference only.");

    public bool limitTractorSpeed
    {
        get => throw new NotSupportedException("Compile-time reference only.");
        set => throw new NotSupportedException("Compile-time reference only.");
    }

    public void ReDoAutoTractor(
        Logic.FarmTileId tileId,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles) =>
        throw new NotSupportedException("Compile-time reference only.");

    public void StartAutoTractor() =>
        throw new NotSupportedException("Compile-time reference only.");

    public override void Update() =>
        throw new NotSupportedException("Compile-time reference only.");

    public override void updateCharacterControllerParameters() =>
        throw new NotSupportedException("Compile-time reference only.");

    public override bool DidPerformWork(
        Logic.WorkType workType,
        Logic.Definition.FarmItemDefinition definition,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles,
        int selectedIndex,
        Logic.WorkFlags workFlags,
        double timestamp) =>
        throw new NotSupportedException("Compile-time reference only.");

    public void StartWorking(
        Logic.WorkType workType,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles,
        Logic.Definition.FarmItemDefinition definition) =>
        throw new NotSupportedException("Compile-time reference only.");

    public void StartWorking(
        Logic.WorkType workType,
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles,
        int selectedIndex,
        Logic.Definition.FarmItemDefinition definition) =>
        throw new NotSupportedException("Compile-time reference only.");

    public void UpdateFarmTiles(
        Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile> tiles,
        bool firstPass,
        bool secondPass,
        bool thirdPass) =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class StageScript : UnityEngine.MonoBehaviour
{
    public StageScript(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public LocalPlayer LocalPlayer =>
        throw new NotSupportedException("Compile-time reference only.");

    public static StageScript Instance =>
        throw new NotSupportedException("Compile-time reference only.");

    public static bool HasInstance =>
        throw new NotSupportedException("Compile-time reference only.");

    public bool HasLocalPlayer =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual bool IsLoaded =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class WidgetOwner : UnityEngine.MonoBehaviour
{
    public WidgetOwner(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class SelectedTiles : WidgetOwner
{
    public SelectedTiles(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class SelectedTilesTractorWork : Il2CppSystem.Object
{
    public SelectedTilesTractorWork(IntPtr pointer) : base(pointer) =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual void Apply(SelectedTiles tiles, LocalPlayer player) =>
        throw new NotSupportedException("Compile-time reference only.");

    public virtual bool Check(SelectedTiles tiles, LocalPlayer player) =>
        throw new NotSupportedException("Compile-time reference only.");
}
