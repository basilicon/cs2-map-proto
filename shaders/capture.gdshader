shader_type spatial;
render_mode unshaded, cull_back, fog_disabled, shadows_disabled;

// Off-screen capture passes (see SceneCapture.cs). mode 0: flat colour, mode 1: distance to the camera, white at
// near_dist to black at far_dist; inverse depth (1/d, like MiDaS maps) unless linear_depth, which suits ortho views.
// The capture camera uses a linear tonemap, so the written pixel is exactly flat_color, or the depth value
// (converted to linear here so it lands unchanged).
uniform int mode = 0;
uniform vec3 flat_color : source_color = vec3(1.0);
uniform float near_dist = 16.0;
uniform float far_dist = 3072.0;
uniform bool linear_depth = false;

void fragment() {
	if (mode == 0) {
		ALBEDO = flat_color;
	} else {
		float d = max(length(VERTEX), 0.001);
		float v = linear_depth
			? 1.0 - (d - near_dist) / max(far_dist - near_dist, 1.0)
			: (1.0 / d - 1.0 / far_dist) / max(1.0 / near_dist - 1.0 / far_dist, 1e-6);
		v = clamp(v, 0.0, 1.0);
		ALBEDO = v <= 0.04045 ? vec3(v / 12.92) : vec3(pow((v + 0.055) / 1.055, 2.4));
	}
}
