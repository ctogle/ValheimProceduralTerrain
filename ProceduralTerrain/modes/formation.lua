-- 3D density field above heightmap, matching C# ModeCDef + tangent isotropy correction
-- gradiso: target feature size (wu) for slope isotropy; 0 = disabled
-- Noise indices: n4=warpX, n5=warpZ, n6=ampVary, n7=twistVary, n8=mainNoise
function bake(wx, wz, params, extLoS, s, bh)
    local warpx = n4(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp
    local warpz = n5(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp
    local addh  = terrain_h(wx + warpx, wz + warpz) - params.yoffset
    local lamp  = math.max(params.amplitude *
                    (1 + params.ampvary * n6(wx * params.ampfrequency, wz * params.ampfrequency, 0)), 0)
    local ts    = 1 + params.twistvary * n7(wx * params.twistfrequency, wz * params.twistfrequency, 0)

    local nwx, nwz = wx, wz
    local gradiso = params.gradiso or 0
    if gradiso > 0 and lamp > 0 then
        local dhdx     = (terrain_h(wx + 2, wz) - terrain_h(wx - 2, wz)) * 0.25
        local dhdz     = (terrain_h(wx, wz + 2) - terrain_h(wx, wz - 2)) * 0.25
        local grad_mag = math.sqrt(dhdx*dhdx + dhdz*dhdz)
        if grad_mag > 0.01 then
            local scale_t = math.max(1.0, gradiso * grad_mag / lamp)
            local inv_gm  = 1.0 / grad_mag
            local gx, gz  = dhdx * inv_gm, dhdz * inv_gm
            local ng      = nwx * gx + nwz * gz
            local nt      = (-nwx * gz + nwz * gx) * scale_t
            nwx = ng * gx - nt * gz
            nwz = ng * gz + nt * gx
        end
    end

    local result = {}
    for cy = 0, bh - 1 do
        local wy   = extLoS + cy * s
        local rely = wy - addh
        local sy   = rely * params.vscale
        local ang  = wy * params.twist * ts
        local c, ss = math.cos(ang), math.sin(ang)
        local n    = n8((nwx*c - nwz*ss) * params.frequency, sy * params.frequency, (nwx*ss + nwz*c) * params.frequency)
        local ft   = math.max(0, math.min(1, rely / math.max(lamp, 0.001)))
        local efft = params.threshold - params.flare * (4 * ft * (1 - ft))
        result[cy + 1] = rely - (n - efft) * lamp
    end
    return result
end
