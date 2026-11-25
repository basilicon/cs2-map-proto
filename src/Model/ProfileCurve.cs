using System;
using System.Collections.Generic;
using System.Linq;

namespace Cs2MapProto.Model;

public sealed class CurveKey
{
    /// <summary>Position along the path, 0 = first point, 1 = last point (by distance).</summary>
    public float T { get; set; }
    public float V { get; set; }

    public CurveKey() { }
    public CurveKey(float t, float v) { T = t; V = v; }
}

/// <summary>A value that varies along a path's length (like Godot's Curve resource, but plain data so the
/// map builder stays engine-independent). Keys are interpolated with a monotone cubic, so the curve never
/// overshoots between keys (no negative widths from a smooth bump).</summary>
public sealed class ProfileCurve
{
    public bool Enabled { get; set; }
    public List<CurveKey> Keys { get; set; } = new();

    public static ProfileCurve Flat(float v) => new() { Keys = { new CurveKey(0, v), new CurveKey(1, v) } };

    /// <summary>Value at t, or fallback when the curve is disabled or empty.</summary>
    public float Sample(float t, float fallback) => Enabled && Keys.Count > 0 ? Evaluate(t) : fallback;

    /// <summary>Value at t regardless of Enabled (used by the curve editor to draw it).</summary>
    public float Evaluate(float t)
    {
        var k = Keys.OrderBy(x => x.T).ToList();
        if (k.Count == 0) return 0;
        if (k.Count == 1 || t <= k[0].T) return k[0].V;
        if (t >= k[^1].T) return k[^1].V;

        // Fritsch-Carlson monotone tangents.
        int n = k.Count;
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (k[i + 1].V - k[i].V) / MathF.Max(1e-6f, k[i + 1].T - k[i].T);
        var m = new float[n];
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) * 0.5f;
        for (int i = 0; i < n - 1; i++)
        {
            if (MathF.Abs(d[i]) < 1e-9f) { m[i] = m[i + 1] = 0; continue; }
            float a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
            if (s > 9) { float tau = 3 / MathF.Sqrt(s); m[i] = tau * a * d[i]; m[i + 1] = tau * b * d[i]; }
        }

        int seg = 0;
        while (seg < n - 2 && t > k[seg + 1].T) seg++;
        float h = MathF.Max(1e-6f, k[seg + 1].T - k[seg].T);
        float u = (t - k[seg].T) / h, u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * k[seg].V + (u3 - 2 * u2 + u) * h * m[seg]
             + (-2 * u3 + 3 * u2) * k[seg + 1].V + (u3 - u2) * h * m[seg + 1];
    }

    public ProfileCurve Clone() => new() { Enabled = Enabled, Keys = Keys.Select(x => new CurveKey(x.T, x.V)).ToList() };
}
