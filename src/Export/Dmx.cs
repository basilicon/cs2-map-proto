using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cs2MapProto.Export;

/// <summary>Minimal Valve DataModel (DMX) element tree, written in the "keyvalues2" text encoding.
/// Hammer's .vmap files are DMX; dmxconvert.exe can turn this text into the binary encoding.</summary>
public sealed class DmElement
{
    public readonly string Type;
    public readonly Guid Id = Guid.NewGuid();
    public readonly List<(string Name, string Type, object Value)> Attributes = new();

    public DmElement(string type) { Type = type; }

    public DmElement Set(string name, string type, object value) { Attributes.Add((name, type, value)); return this; }

    public DmElement Str(string name, string v) => Set(name, "string", v ?? "");
    public DmElement Int(string name, int v) => Set(name, "int", v);
    public DmElement Float(string name, float v) => Set(name, "float", v);
    public DmElement Bool(string name, bool v) => Set(name, "bool", v);
    public DmElement Vec2(string name, Vector2 v) => Set(name, "vector2", v);
    public DmElement Vec3(string name, Vector3 v) => Set(name, "vector3", v);
    public DmElement QAngle(string name, Vector3 v) => Set(name, "qangle", v);
    public DmElement Color(string name, byte r, byte g, byte b, byte a) => Set(name, "color", $"{r} {g} {b} {a}");
    public DmElement UInt64(string name, ulong v) => Set(name, "uint64", v);
    public DmElement Element(string name, DmElement e) => Set(name, e?.Type ?? "element", e);
    public DmElement Elements(string name, IEnumerable<DmElement> e) => Set(name, "element_array", new List<DmElement>(e));
    public DmElement Ints(string name, IEnumerable<int> v) => Set(name, "int_array", new List<int>(v));
    public DmElement Strings(string name, IEnumerable<string> v) => Set(name, "string_array", new List<string>(v));
    public DmElement Vec2s(string name, IEnumerable<Vector2> v) => Set(name, "vector2_array", new List<Vector2>(v));
    public DmElement Vec3s(string name, IEnumerable<Vector3> v) => Set(name, "vector3_array", new List<Vector3>(v));
    public DmElement Vec4s(string name, IEnumerable<Vector4> v) => Set(name, "vector4_array", new List<Vector4>(v));
}

public static class KeyValues2Writer
{
    public static string Write(string format, int formatVersion, params DmElement[] roots)
    {
        var sb = new StringBuilder(1 << 20);
        sb.Append("<!-- dmx encoding keyvalues2 4 format ").Append(format).Append(' ').Append(formatVersion).Append(" -->\n");
        foreach (var r in roots)
        {
            WriteElement(sb, r, 0);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static void Indent(StringBuilder sb, int n) => sb.Append('\t', n);

    static void WriteElement(StringBuilder sb, DmElement e, int depth)
    {
        Indent(sb, depth); sb.Append('"').Append(e.Type).Append("\"\n");
        Indent(sb, depth); sb.Append("{\n");
        Indent(sb, depth + 1); sb.Append("\"id\" \"elementid\" \"").Append(e.Id.ToString()).Append("\"\n");
        foreach (var (name, type, value) in e.Attributes)
            WriteAttribute(sb, name, type, value, depth + 1);
        Indent(sb, depth); sb.Append('}');
    }

    static void WriteAttribute(StringBuilder sb, string name, string type, object value, int depth)
    {
        Indent(sb, depth);
        sb.Append('"').Append(name).Append("\" ");
        switch (value)
        {
            case DmElement child:
                sb.Append('"').Append(child.Type).Append("\"\n");
                // Inline element body (re-indent the header-less form).
                Indent(sb, depth); sb.Append("{\n");
                Indent(sb, depth + 1); sb.Append("\"id\" \"elementid\" \"").Append(child.Id.ToString()).Append("\"\n");
                foreach (var (n, t, v) in child.Attributes) WriteAttribute(sb, n, t, v, depth + 1);
                Indent(sb, depth); sb.Append("}\n\n");
                return;
            case null when type != "string":
                sb.Append("\"element\" \"\"\n");
                return;
            case List<DmElement> els:
                sb.Append("\"element_array\" \n");
                Indent(sb, depth); sb.Append("[\n");
                for (int i = 0; i < els.Count; i++)
                {
                    WriteElement(sb, els[i], depth + 1);
                    sb.Append(i < els.Count - 1 ? ",\n" : "\n");
                }
                Indent(sb, depth); sb.Append("]\n");
                return;
            case System.Collections.IList list:
                sb.Append('"').Append(type).Append("\" \n");
                Indent(sb, depth); sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    Indent(sb, depth + 1);
                    sb.Append('"').Append(Format(list[i])).Append('"');
                    sb.Append(i < list.Count - 1 ? ",\n" : "\n");
                }
                Indent(sb, depth); sb.Append("]\n");
                return;
            default:
                sb.Append('"').Append(type).Append("\" \"").Append(Format(value)).Append("\"\n");
                return;
        }
    }

    static string F(float f)
    {
        if (MathF.Abs(f) < 1e-7f) f = 0;
        return f.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    static string Format(object v) => v switch
    {
        null => "",
        string s => Escape(s),
        bool b => b ? "1" : "0",
        int i => i.ToString(CultureInfo.InvariantCulture),
        float f => F(f),
        ulong u => "0x" + u.ToString("x", CultureInfo.InvariantCulture),
        Vector2 a => $"{F(a.X)} {F(a.Y)}",
        Vector3 a => $"{F(a.X)} {F(a.Y)} {F(a.Z)}",
        Vector4 a => $"{F(a.X)} {F(a.Y)} {F(a.Z)} {F(a.W)}",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
