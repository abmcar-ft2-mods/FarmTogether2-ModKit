using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object(IntPtr pointer) : base(pointer) { }
    }

    public class ValueType : Object
    {
        public ValueType(IntPtr pointer) : base(pointer) { }
    }
}

namespace Il2CppSystem.Collections.Generic
{
    public class List<T> : Il2CppSystem.Object
    {
        public List(IntPtr pointer) : base(pointer) { }
        public int Count => throw new NotSupportedException("Compile-time reference only.");
        public T this[int index] => throw new NotSupportedException("Compile-time reference only.");
        public void Add(T item) => throw new NotSupportedException("Compile-time reference only.");
    }
}
