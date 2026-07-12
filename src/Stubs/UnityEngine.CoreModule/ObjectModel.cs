namespace UnityEngine;

public class Object : Il2CppSystem.Object
{
    public Object(IntPtr pointer) : base(pointer) { }
    public int GetInstanceID() => throw new NotSupportedException("Compile-time reference only.");
}

public class Component : Object
{
    public Component(IntPtr pointer) : base(pointer) { }
    public T GetComponentInParent<T>() where T : Component =>
        throw new NotSupportedException("Compile-time reference only.");
}

public class Behaviour : Component
{
    public Behaviour(IntPtr pointer) : base(pointer) { }
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

public class Texture2D : Texture
{
    public Texture2D(IntPtr pointer) : base(pointer) { }
    public static Texture2D whiteTexture => throw new NotSupportedException("Compile-time reference only.");
}
