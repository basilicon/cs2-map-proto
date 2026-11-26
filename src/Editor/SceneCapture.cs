using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Cs2MapProto.Plugins;

namespace Cs2MapProto.Editor;

/// <summary>Renders the built map off screen for plugins: a SubViewport sharing the editor's 3D world, with
/// its own camera. Each pass picks the visual layer of the matching PreviewRenderer copy, so the editor's
/// view, overlay and materials are never touched. Captures run one at a time.</summary>
public partial class SceneCapture : Node
{
    public PreviewRenderer Preview { get; set; }

    SubViewport _viewport;
    Camera3D _camera;
    Godot.Environment _flatSky, _flatBlack;
    readonly SemaphoreSlim _gate = new(1, 1);

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            Name = "CaptureViewport",
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Size = new Vector2I(768, 512),
        };
        AddChild(_viewport);
        _camera = new Camera3D { Name = "CaptureCamera", Current = true, Near = 2f, Far = 100000f };
        _viewport.AddChild(_camera);

        Godot.Environment Flat(Color bg) => new()
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = bg,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
        };
        _flatSky = Flat(CapturePalette.Sky);
        _flatBlack = Flat(Colors.Black);
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest r)
    {
        await _gate.WaitAsync();
        try
        {
            _viewport.Size = new Vector2I(Math.Clamp(r.Width, 16, 4096), Math.Clamp(r.Height, 16, 4096));
            var eye = Space.ToGodot(r.Eye);
            var target = Space.ToGodot(r.Target);
            var dir = (target - eye).Normalized();
            var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up; // straight down: north is up
            _camera.LookAtFromPosition(eye, target, up);
            if (r.OrthoSize > 0) { _camera.Projection = Camera3D.ProjectionType.Orthogonal; _camera.Size = r.OrthoSize; }
            else { _camera.Projection = Camera3D.ProjectionType.Perspective; _camera.Fov = Math.Clamp(r.FovDegrees, 5f, 170f); }

            var result = new CaptureResult
            {
                Width = _viewport.Size.X, Height = _viewport.Size.Y,
                Projection = _camera.GetCameraProjection(), View = _camera.GlobalTransform.AffineInverse(),
                Ortho = r.OrthoSize > 0,
            };
            if (r.Passes.HasFlag(CapturePass.Beauty))
                result.Beauty = await Render(PreviewRenderer.PreviewLayer, null, Viewport.Msaa.Msaa4X);
            if (r.Passes.HasFlag(CapturePass.Semantic))
                result.Semantic = await Render(PreviewRenderer.SemanticLayer, _flatSky, Viewport.Msaa.Disabled);
            if (r.Passes.HasFlag(CapturePass.Depth))
            {
                Preview.DepthMaterial?.SetShaderParameter("near_dist", r.DepthNear);
                Preview.DepthMaterial?.SetShaderParameter("far_dist", r.DepthFar);
                Preview.DepthMaterial?.SetShaderParameter("linear_depth", r.OrthoSize > 0);
                result.Depth = await Render(PreviewRenderer.DepthLayer, _flatBlack, Viewport.Msaa.Msaa4X);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    async Task<Image> Render(uint layers, Godot.Environment env, Viewport.Msaa msaa)
    {
        _camera.CullMask = layers;
        _camera.Environment = env;
        _viewport.Msaa3D = msaa;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        // One frame to draw it, one more so the texture readback sees the finished frame.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = _viewport.GetTexture().GetImage();
        img.Convert(Image.Format.Rgb8);
        return img;
    }
}
