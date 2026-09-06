-- Visualizes raw n8 noise field in XZ at a fixed rely depth.
-- Carves where n8 > 0, solid where n8 < 0 — no terrain coupling, no warp.
-- View top-down to see the pure noise XZ pattern.
function bake(wx, wz, params, extLoS, s, bh)
    local addh = terrain_h(wx, wz)
    local n    = n8(wx * params.frequency, 0, wz * params.frequency)
    local result = {}
    for cy = 0, bh - 1 do
        local wy   = extLoS + cy * s
        local rely = wy - addh
        result[cy + 1] = rely - n * params.amplitude
    end
    return result
end
