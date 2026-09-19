function bake(wx, wz, params, extLoS, s, bh)
    local sr = params.smoothradius or 0
    local th
    if sr > 0 then
        local sum, pts = 0, 0
        for dx = -1, 1 do
        for dz = -1, 1 do
            sum = sum + terrain_h(wx + dx * sr, wz + dz * sr)
            pts = pts + 1
        end end
        th = sum / pts
    else
        th = terrain_h(wx, wz)
    end
    local result = {}
    for cy = 0, bh - 1 do
        local wy = extLoS + cy * s
        result[cy + 1] = wy - th
    end
    return result
end
