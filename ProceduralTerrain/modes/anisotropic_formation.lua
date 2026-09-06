-- Coast-aligned anisotropic noise ridges (equivalent to type D)
-- Noise indices: n10=mdNoise
function bake(wx, wy, wz, params)
    local g      = terrain_grad(wx, wz)
    local dirx, dirz, mag, blurh = g.dirx, g.dirz, g.mag, g.blurh
    local gf     = params.gradfade > 0 and smoothstep(0, params.gradfade, mag) or 1
    local freqt  = lerp(params.freqt, params.freqn, params.alignment)
    local freqn  = lerp(params.freqn, params.freqt, params.alignment)
    local tc     = (wx * (-dirz) + wz * dirx) * freqt * gf
    local nc     = (blurh / math.max(params.blurradius, 1)) * freqn * gf
    local sf     = params.seafade > 0 and smoothstep(0, params.seafade, terrain_h(wx, wz) - 30) or 1
    local mdn    = n10(tc, wy * params.vscale, nc) * 0.5 + 0.5
    return (wy - terrain_h(wx, wz)) - mdn * params.amplitude * sf
end
