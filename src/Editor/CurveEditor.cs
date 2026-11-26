using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Cs2MapProto.Model;

namespace Cs2MapProto.Editor;

/// <summary>Small runtime curve editor (Godot's own Curve editor only exists inside the Godot editor).
/// X = position along the path (0..1), Y = value between MinValue and MaxValue.
/// Click empty space to add a key, drag keys to move them, right-click a key to remove it.
/// The first and last keys stay pinned to the path's start and end.</summary>
public partial class CurveEditor : Control
{
    [Export] public float MinValue { get; set; } = 0f;
    [Export] public float MaxValue { get; set; } = 2f;
    [Export] public float DefaultValue { get; set; } = 1f;
    [Export] public string Unit { get; set; } = "x";
    [Export] public bool Active { get; set; } = true; // drawn dimmed when the curve is disabled

    public ProfileCurve Curve { get; private set; } = ProfileCurve.Flat(1);
    public bool IsDragging => _drag >= 0;

    /// <summary>Raised once before a change (for undo), then Changed while editing.</summary>
    public event Action EditStarted, Changed;

    int _drag = -1, _hover = -1;
    const float KeyRadius = 6f;
    const float Pad = 8f;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(Math.Max(CustomMinimumSize.X, 260), Math.Max(CustomMinimumSize.Y, 130));
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
    }

    public void SetCurve(ProfileCurve curve, float min, float max, float def, string unit, bool active)
    {
        if (IsDragging) return;
        Curve = curve.Clone();
        MinValue = min; MaxValue = max; DefaultValue = def; Unit = unit; Active = active;
        QueueRedraw();
    }

    Rect2 Plot => new(Pad, Pad, Size.X - 2 * Pad, Size.Y - 2 * Pad - 12);
    Vector2 ToScreen(float t, float v) => new(Plot.Position.X + t * Plot.Size.X, Plot.End.Y - (v - MinValue) / (MaxValue - MinValue) * Plot.Size.Y);
    (float t, float v) FromScreen(Vector2 p) => (
        Math.Clamp((p.X - Plot.Position.X) / Plot.Size.X, 0f, 1f),
        Math.Clamp(MinValue + (Plot.End.Y - p.Y) / Plot.Size.Y * (MaxValue - MinValue), MinValue, MaxValue));

    List<CurveKey> Sorted => Curve.Keys.OrderBy(k => k.T).ToList();

    int KeyAt(Vector2 p)
    {
        var keys = Sorted;
        for (int i = 0; i < keys.Count; i++)
            if (ToScreen(keys[i].T, keys[i].V).DistanceTo(p) <= KeyRadius + 3) return i;
        return -1;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
            {
                Curve.Keys = Sorted;
                int k = KeyAt(mb.Position);
                EditStarted?.Invoke();
                if (k < 0)
                {
                    var (t, v) = FromScreen(mb.Position);
                    Curve.Keys.Add(new CurveKey(t, v));
                    Curve.Keys = Sorted;
                    k = Curve.Keys.FindIndex(x => x.T == t && x.V == v);
                    Changed?.Invoke();
                }
                _drag = k;
                AcceptEvent();
                QueueRedraw();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _drag = -1;
                QueueRedraw();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } mb:
            {
                Curve.Keys = Sorted;
                int k = KeyAt(mb.Position);
                if (k > 0 && k < Curve.Keys.Count - 1)
                {
                    EditStarted?.Invoke();
                    Curve.Keys.RemoveAt(k);
                    Changed?.Invoke();
                    QueueRedraw();
                }
                AcceptEvent();
                break;
            }
            case InputEventMouseMotion mm:
                if (_drag >= 0)
                {
                    var keys = Curve.Keys;
                    var (t, v) = FromScreen(mm.Position);
                    // Ends stay pinned at t = 0 / 1; inner keys can't pass their neighbours.
                    if (_drag == 0) t = 0;
                    else if (_drag == keys.Count - 1) t = 1;
                    else t = Math.Clamp(t, keys[_drag - 1].T + 0.005f, keys[_drag + 1].T - 0.005f);
                    keys[_drag].T = t; keys[_drag].V = MathF.Round(v * 100f) / 100f;
                    Changed?.Invoke();
                    QueueRedraw();
                    AcceptEvent();
                }
                else
                {
                    int h = KeyAt(mm.Position);
                    if (h != _hover) { _hover = h; QueueRedraw(); }
                }
                break;
        }
    }

    public override void _Draw()
    {
        var plot = Plot;
        var font = GetThemeDefaultFont();
        int fs = 11;
        float alpha = Active ? 1f : 0.45f;

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.078f, 0.086f, 0.102f)); // matches the theme's field background
        for (int i = 0; i <= 4; i++)
        {
            float x = plot.Position.X + plot.Size.X * i / 4f;
            DrawLine(new Vector2(x, plot.Position.Y), new Vector2(x, plot.End.Y), new Color(1, 1, 1, 0.05f));
        }
        // Reference line at the default value (1x width / 0 offset).
        float defY = ToScreen(0, DefaultValue).Y;
        DrawDashedLine(new Vector2(plot.Position.X, defY), new Vector2(plot.End.X, defY), new Color(1, 1, 1, 0.22f), 1f, 4f);
        DrawRect(plot, new Color(1, 1, 1, 0.08f), false);

        var pts = new Vector2[65];
        for (int i = 0; i < pts.Length; i++)
        {
            float t = i / 64f;
            pts[i] = ToScreen(t, Math.Clamp(Curve.Evaluate(t), MinValue, MaxValue));
        }
        DrawPolyline(pts, new Color(0.45f, 0.85f, 1f, alpha), 2f, true);

        var keys = Sorted;
        for (int i = 0; i < keys.Count; i++)
        {
            var c = i == _drag ? new Color(1, 1, 0.3f) : i == _hover ? Colors.White : new Color(0.45f, 0.85f, 1f, alpha);
            DrawCircle(ToScreen(keys[i].T, keys[i].V), i == _drag || i == _hover ? KeyRadius : KeyRadius - 1.5f, c);
        }

        var dim = new Color(0.43f, 0.46f, 0.51f);
        DrawString(font, new Vector2(plot.Position.X + 2, plot.Position.Y + fs), $"{MaxValue:0.##}{Unit}", HorizontalAlignment.Left, -1, fs, dim);
        DrawString(font, new Vector2(plot.Position.X + 2, plot.End.Y - 3), $"{MinValue:0.##}{Unit}", HorizontalAlignment.Left, -1, fs, dim);
        DrawString(font, new Vector2(plot.Position.X, Size.Y - 3), "start", HorizontalAlignment.Left, -1, fs, dim);
        DrawString(font, new Vector2(plot.End.X - 24, Size.Y - 3), "end", HorizontalAlignment.Left, -1, fs, dim);
        if (_drag >= 0 && _drag < keys.Count)
        {
            var k = keys[_drag];
            DrawString(font, new Vector2(plot.Position.X + plot.Size.X * 0.5f - 40, Size.Y - 3), $"{k.T * 100:0}%  {k.V:0.##}{Unit}", HorizontalAlignment.Left, -1, fs, Colors.White);
        }
    }
}
