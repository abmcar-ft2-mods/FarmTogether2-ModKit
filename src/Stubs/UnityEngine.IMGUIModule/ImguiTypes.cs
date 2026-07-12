namespace UnityEngine;

public class GUISkin : ScriptableObject
{
    public GUISkin(IntPtr pointer) : base(pointer) { }
    public GUIStyle label => throw new NotSupportedException("Compile-time reference only.");
}

public class GUIStyle : Il2CppSystem.Object
{
    public GUIStyle(IntPtr pointer) : base(pointer) { }
    public GUIStyle(GUIStyle other) : base(IntPtr.Zero) { }
    public int fontSize { get; set; }
    public FontStyle fontStyle { get; set; }
    public TextAnchor alignment { get; set; }
    public bool wordWrap { get; set; }
    public GUIStyleState normal => throw new NotSupportedException("Compile-time reference only.");
}

public class GUIStyleState : Il2CppSystem.Object
{
    public GUIStyleState(IntPtr pointer) : base(pointer) { }
    public Color textColor { get; set; }
}

public class GUI : Il2CppSystem.Object
{
    public GUI(IntPtr pointer) : base(pointer) { }
    public static Color color { get; set; }
    public static GUISkin skin => throw new NotSupportedException("Compile-time reference only.");
    public static void Label(Rect position, string text) => throw new NotSupportedException("Compile-time reference only.");
    public static void Label(Rect position, string text, GUIStyle style) => throw new NotSupportedException("Compile-time reference only.");
    public static void DrawTexture(Rect position, Texture texture) => throw new NotSupportedException("Compile-time reference only.");
}
