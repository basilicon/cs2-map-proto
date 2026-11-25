using Godot;
using Cs2MapProto.Model;

namespace Cs2MapProto.Editor;

/// <summary>Hammer (X east, Y north, Z up) &lt;-&gt; Godot (Y up, -Z forward). 1 Godot unit = 1 Hammer unit.
/// The mapping is a proper rotation, so polygon winding is preserved.</summary>
public static class Space
{
    public static Vector3 ToGodot(float x, float y, float z) => new(x, z, -y);
    public static Vector3 ToGodot(Vec3 v) => ToGodot(v.X, v.Y, v.Z);
    public static Vector3 ToGodot(System.Numerics.Vector3 v) => ToGodot(v.X, v.Y, v.Z);
    public static Vec3 ToHammer(Vector3 g) => new(g.X, -g.Z, g.Y);
}
