-- Warped-heightmap band mode (equivalent to type B)
-- Noise indices: n4=hmAddWarpX, n5=hmAddWarpZ, n9=mbNoise
function bake(wx, wy, wz, params)
    local warpx = n4(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp
    local warpz = n5(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp
    local wh    = terrain_h(wx + warpx, wz + warpz) - params.yoffset
    local band  = params.amplitude - math.abs(wy - wh)
    if params.noiseamp > 0 then
        local ang  = wy * params.twist
        local c, s = math.cos(ang), math.sin(ang)
        band = band + n9(
            (wx*c - wz*s) * params.warpfrequency,
            wy * params.vscale * params.warpfrequency,
            (wx*s + wz*c) * params.warpfrequency) * params.noiseamp
    end
    return band
end
