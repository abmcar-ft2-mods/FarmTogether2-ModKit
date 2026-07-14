namespace UnityEngine;

public class Time : Il2CppSystem.Object
{
    public Time(IntPtr pointer) : base(pointer) { }
    public static float realtimeSinceStartup => throw new NotSupportedException("Compile-time reference only.");
}

public sealed class Screen : Il2CppSystem.Object
{
    public Screen(IntPtr pointer) : base(pointer) { }
    public static int width => throw new NotSupportedException("Compile-time reference only.");
}

public enum KeyCode
{
    Comma = 44,
    Minus = 45,
    Period = 46,
    Equals = 61,
    LeftBracket = 91,
    RightBracket = 93,
    F8 = 289,
    F9 = 290,
    F10 = 291
}
