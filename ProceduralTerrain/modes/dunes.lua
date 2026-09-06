-- Dunes: sinusoidal ridges aligned along the terrain gradient tangent.
-- blend = min  (adds solid terrain above vanilla surface)
function bake(wx, wz, params, extLoS, s, bh)
    local dirx, dirz = params.grad_dirx, params.grad_dirz
    local t          = wx * (-dirz) + wz * dirx
    local sine       = math.sin(t * params.frequency)
    local pinched    = sine >= 0 and sine^params.pinch or -((-sine)^params.pinch)
    local dune_top   = terrain_h(wx, wz) + params.amplitude * (0.5 + 0.5 * pinched)
    local result = {}
    for cy = 0, bh - 1 do
        result[cy + 1] = (extLoS + cy * s) - dune_top
    end
    return result
end
