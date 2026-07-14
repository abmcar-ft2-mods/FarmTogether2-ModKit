namespace UnityEngine;

#pragma warning disable CS0660, CS0661
public class Object : Il2CppSystem.Object
{
    public Object(IntPtr pointer) : base(pointer) { }
    public int GetInstanceID() => throw new NotSupportedException("Compile-time reference only.");
    public static void Destroy(Object obj) =>
        throw new NotSupportedException("Compile-time reference only.");
    public static bool operator ==(Object? left, Object? right) =>
        throw new NotSupportedException("Compile-time reference only.");
    public static bool operator !=(Object? left, Object? right) =>
        throw new NotSupportedException("Compile-time reference only.");
}
#pragma warning restore CS0660, CS0661

public class Component : Object
{
    public Component(IntPtr pointer) : base(pointer) { }
    public T GetComponentInParent<T>() =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class Behaviour : Component
{
    public Behaviour(IntPtr pointer) : base(pointer) { }

    public bool enabled
    {
        get => throw new NotSupportedException("Compile-time reference only.");
        set => throw new NotSupportedException("Compile-time reference only.");
    }
}

public class MonoBehaviour : Behaviour
{
    public MonoBehaviour(IntPtr pointer) : base(pointer) { }
}

public class ScriptableObject : Object
{
    public ScriptableObject(IntPtr pointer) : base(pointer) { }
}

public class Texture : Object
{
    public Texture(IntPtr pointer) : base(pointer) { }
}

public sealed class Texture2D : Texture
{
    public Texture2D(IntPtr pointer) : base(pointer) { }
    public static Texture2D whiteTexture => throw new NotSupportedException("Compile-time reference only.");
}
