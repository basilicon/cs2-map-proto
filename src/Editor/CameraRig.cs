using Godot;

namespace Cs2MapProto.Editor;

/// <summary>Two camera modes:
///  - Top-down: orthographic, straight down. Middle-drag (or Space+left-drag) pans, wheel zooms, WASD pans.
///  - Free: perspective fly cam. Hold right mouse to look, WASD + Q/E to move, Shift = fast, wheel = speed.</summary>
public partial class CameraRig : Node3D
{
    [Export] public Camera3D Camera { get; set; }
    [Export] public float TopDownSize { get; set; } = 4096f;
    [Export] public float FlySpeed { get; set; } = 900f;

    public bool IsTopDown { get; private set; } = true;
    public bool IsLooking { get; private set; }

    /// <summary>Raised for a short right-click in free-cam mode (where holding the button means "look").</summary>
    public event System.Action<Vector2> RightClicked;
    Vector2 _rmbPressPos;
    float _rmbMoved;
    ulong _rmbPressMs;

    Vector3 _topFocus = Vector3.Zero; // Godot space
    float _yaw = 0f, _pitch = -0.6f;
    Vector3 _freePos = new(0, 900, 1400);

    public override void _Ready()
    {
        Camera ??= GetNodeOrNull<Camera3D>("Camera3D");
        Camera.Near = 2f;
        Camera.Far = 100000f;
        ApplyTopDown();
    }

    public void ToggleMode()
    {
        if (IsTopDown)
        {
            // Start the free cam looking at what the top view was showing.
            _freePos = _topFocus + new Vector3(0, TopDownSize * 0.35f, TopDownSize * 0.45f);
            _yaw = 0; _pitch = -0.65f;
            IsTopDown = false;
            ApplyFree();
        }
        else
        {
            var fwd = -Camera.GlobalBasis.Z;
            float t = fwd.Y < -0.05f ? (_freePos.Y - _topFocus.Y) / -fwd.Y : 1000f;
            _topFocus = new Vector3(_freePos.X + fwd.X * t, _topFocus.Y, _freePos.Z + fwd.Z * t);
            IsTopDown = true;
            SetLooking(false);
            ApplyTopDown();
        }
    }

    public void Frame(Aabb box)
    {
        _topFocus = box.GetCenter();
        TopDownSize = Mathf.Max(512f, Mathf.Max(box.Size.X, box.Size.Z) * 1.2f);
        if (IsTopDown) ApplyTopDown();
        else { _freePos = _topFocus + new Vector3(0, TopDownSize * 0.35f, TopDownSize * 0.45f); _yaw = 0; _pitch = -0.65f; ApplyFree(); }
    }

    /// <summary>Switches to the free cam at pos looking at target (Godot space).</summary>
    public void LookFrom(Vector3 pos, Vector3 target)
    {
        var dir = (target - pos).Normalized();
        _freePos = pos;
        _yaw = Mathf.Atan2(-dir.X, -dir.Z);
        _pitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
        IsTopDown = false;
        ApplyFree();
    }

    /// <summary>World units covered by one screen pixel at a given point (for handle sizes and drag scaling).</summary>
    public float WorldPerPixel(Vector3 at)
    {
        float h = GetViewport().GetVisibleRect().Size.Y;
        if (IsTopDown) return TopDownSize / h;
        float d = Camera.GlobalPosition.DistanceTo(at);
        return d * 2f * Mathf.Tan(Mathf.DegToRad(Camera.Fov) * 0.5f) / h;
    }

    void ApplyTopDown()
    {
        Camera.Projection = Camera3D.ProjectionType.Orthogonal;
        Camera.Size = TopDownSize;
        Camera.GlobalTransform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2f), _topFocus + Vector3.Up * 20000f);
    }

    void ApplyFree()
    {
        Camera.Projection = Camera3D.ProjectionType.Perspective;
        Camera.Fov = 75f;
        var basis = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch);
        Camera.GlobalTransform = new Transform3D(basis, _freePos);
    }

    void SetLooking(bool on)
    {
        IsLooking = on;
        Input.MouseMode = on ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }

    /// <summary>Returns true if the event was consumed by camera navigation.</summary>
    public bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown:
                if (!mb.Pressed || mb.ShiftPressed || mb.CtrlPressed) return false;
                float f = mb.ButtonIndex == MouseButton.WheelUp ? 0.85f : 1f / 0.85f;
                if (IsTopDown)
                {
                    // Zoom towards the cursor.
                    var before = Camera.ProjectPosition(mb.Position, 1000f);
                    TopDownSize = Mathf.Clamp(TopDownSize * f, 128f, 65536f);
                    ApplyTopDown();
                    var after = Camera.ProjectPosition(mb.Position, 1000f);
                    _topFocus += new Vector3(before.X - after.X, 0, before.Z - after.Z);
                    ApplyTopDown();
                }
                else FlySpeed = Mathf.Clamp(FlySpeed / f, 50f, 20000f);
                return true;

            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Right && !IsTopDown:
                if (mb.Pressed)
                {
                    _rmbPressPos = mb.Position; _rmbMoved = 0; _rmbPressMs = Time.GetTicksMsec();
                    SetLooking(true);
                }
                else
                {
                    SetLooking(false);
                    // A quick click without looking around counts as a right-click (e.g. delete a point).
                    if (_rmbMoved < 6f && Time.GetTicksMsec() - _rmbPressMs < 350)
                    {
                        Input.WarpMouse(_rmbPressPos);
                        RightClicked?.Invoke(_rmbPressPos);
                    }
                }
                return true;

            case InputEventMouseMotion mm when IsLooking:
                _rmbMoved += mm.Relative.Length();
                _yaw -= mm.Relative.X * 0.004f;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.004f, -1.55f, 1.55f);
                ApplyFree();
                return true;

            case InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Middle) != 0
                                            || ((mm.ButtonMask & MouseButtonMask.Left) != 0 && Input.IsKeyPressed(Key.Space)):
                if (IsTopDown)
                {
                    float wpp = WorldPerPixel(_topFocus);
                    _topFocus += new Vector3(-mm.Relative.X * wpp, 0, -mm.Relative.Y * wpp);
                    ApplyTopDown();
                }
                else
                {
                    float wpp = FlySpeed * 0.002f;
                    _freePos += (-Camera.GlobalBasis.X * mm.Relative.X + Camera.GlobalBasis.Y * mm.Relative.Y) * wpp;
                    ApplyFree();
                }
                return true;
        }
        return false;
    }

    public override void _Process(double delta)
    {
        // WASD only when no text field has focus and Ctrl isn't held (Ctrl+S etc. are shortcuts).
        if (GetViewport().GuiGetFocusOwner() is LineEdit or SpinBox or TextEdit) return;
        if (Input.IsKeyPressed(Key.Ctrl)) return;
        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move.Z -= 1;
        if (Input.IsKeyPressed(Key.S)) move.Z += 1;
        if (Input.IsKeyPressed(Key.A)) move.X -= 1;
        if (Input.IsKeyPressed(Key.D)) move.X += 1;
        if (!IsTopDown)
        {
            if (Input.IsKeyPressed(Key.E)) move.Y += 1;
            if (Input.IsKeyPressed(Key.Q)) move.Y -= 1;
        }
        if (move == Vector3.Zero) return;
        float speed = (Input.IsKeyPressed(Key.Shift) ? 3f : 1f) * (float)delta;
        if (IsTopDown)
        {
            _topFocus += new Vector3(move.X, 0, move.Z) * TopDownSize * 0.6f * speed;
            ApplyTopDown();
        }
        else
        {
            var b = Camera.GlobalBasis;
            _freePos += (b.X * move.X + b.Z * move.Z + Vector3.Up * move.Y) * FlySpeed * speed;
            ApplyFree();
        }
    }
}
