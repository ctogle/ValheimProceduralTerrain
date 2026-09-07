-- Warped-heightmap band parity with C# ModeBDef.
-- Noise slots: n16=warpX, n15=warpZ, n14=bandNoise
-- params: amplitude warpAmp warpFrequency vScale noiseAmp noiseFrequency yOffset twist
function bake(wx, wz, params, extLoS, s, bh)
    local amp   = params.amplitude      or 0
    local warpA = params.warpamp        or 0
    local warpF = params.warpfrequency  or 0.005
    local vsc   = params.vscale         or 0.5
    local nAmp  = params.noiseamp       or 0
    local nFreq = params.noisefrequency or warpF
    local yo    = params.yoffset        or 0
    local tw    = params.twist          or 0

    local dx = n16(wx * warpF, wz * warpF, 0) * warpA
    local dz = n15(wx * warpF, wz * warpF, 0) * warpA
    local wh = terrain_h(wx + dx, wz + dz) - yo

    local result = {}
    for cy = 0, bh - 1 do
        local wy  = extLoS + cy * s
        local band = amp - math.abs(wy - wh)
        if nAmp > 0 then
            local ang   = wy * tw
            local c, ss = math.cos(ang), math.sin(ang)
            local nx, nz = wx * c - wz * ss, wx * ss + wz * c
            band = band + n14(nx * nFreq, wy * vsc * nFreq, nz * nFreq) * nAmp
        end
        result[cy + 1] = band
    end
    return result
end
