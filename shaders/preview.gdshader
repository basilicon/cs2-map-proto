shader_type spatial;
render_mode cull_back;

// World-space grid so scale is readable at a glance (Hammer units: 64u minor, 512u major).
uniform vec3 base_color : source_color = vec3(0.6);
uniform float minor = 64.0;
uniform float major = 512.0;
// Meshes whose lowest point (UV2.x, Hammer Z) is at or above this aren't drawn (top view of a lower level).
uniform float hide_above = 1e9;

varying vec3 world_pos;
varying vec3 world_nrm;
varying float mesh_base;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_nrm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	mesh_base = UV2.x;
}

float grid_line(vec2 uv, float size) {
	vec2 c = uv / size;
	vec2 g = abs(fract(c - 0.5) - 0.5) / max(fwidth(c), vec2(1e-5));
	return 1.0 - min(min(g.x, g.y), 1.0);
}

void fragment() {
	if (mesh_base >= hide_above) discard;
	vec3 n = abs(world_nrm);
	vec2 uv = n.y > 0.5 ? world_pos.xz : (n.x > 0.5 ? world_pos.zy : world_pos.xy);
	float l = max(grid_line(uv, minor) * 0.35, grid_line(uv, major) * 0.8);
	// Slightly darken steep faces so walls read differently from floors in the top view.
	vec3 col = base_color * mix(0.8, 1.0, n.y);
	ALBEDO = mix(col, col * 0.45, l);
	ROUGHNESS = 0.95;
}
