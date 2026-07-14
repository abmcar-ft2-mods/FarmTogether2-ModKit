using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object(IntPtr pointer) : base(pointer) { }
    }

#pragma warning disable CS0660, CS0661
    public class Delegate : Object
    {
        public Delegate(IntPtr pointer) : base(pointer) { }

        public static bool operator ==(Delegate? left, Delegate? right) =>
            throw new NotSupportedException("Compile-time reference only.");

        public static bool operator !=(Delegate? left, Delegate? right) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
#pragma warning restore CS0660, CS0661

    public class MulticastDelegate : Delegate
    {
        public MulticastDelegate(IntPtr pointer) : base(pointer) { }
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
        public virtual int Count => throw new NotSupportedException("Compile-time reference only.");
        public virtual T this[int index] => throw new NotSupportedException("Compile-time reference only.");
        public virtual void Add(T item) => throw new NotSupportedException("Compile-time reference only.");
        public virtual void RemoveAt(int index) =>
            throw new NotSupportedException("Compile-time reference only.");
    }
}
