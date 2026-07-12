namespace UnityEngine;

public struct Vector2
{
    public float x;
    public float y;

    public Vector2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public float magnitude => throw new NotSupportedException("Compile-time reference only.");
}

public struct Vector3
{
    public float x;
    public float y;
    public float z;

    public static Vector3 zero => throw new NotSupportedException("Compile-time reference only.");
}

public struct Rect
{
    public Rect(float x, float y, float width, float height)
    {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public float x { get; set; }
    public float y { get; set; }
    public float width { get; set; }
    public float height { get; set; }
}

public struct Color
{
    public Color(float r, float g, float b, float a)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public float r;
    public float g;
    public float b;
    public float a;

    public static Color black => throw new NotSupportedException("Compile-time reference only.");
    public static Color white => throw new NotSupportedException("Compile-time reference only.");
}

public static class Mathf
{
    public static float Clamp(float value, float min, float max) =>
        throw new NotSupportedException("Compile-time reference only.");
    public static float Min(float a, float b) => throw new NotSupportedException("Compile-time reference only.");
    public static float Max(float a, float b) => throw new NotSupportedException("Compile-time reference only.");
    public static float Round(float value) => throw new NotSupportedException("Compile-time reference only.");
    public static int RoundToInt(float value) => throw new NotSupportedException("Compile-time reference only.");
}
