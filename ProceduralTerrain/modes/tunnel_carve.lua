-- Tunnel network parity with C# ModeADef.
-- Noise slots: n9/n10 = tunnel fields, n11 = width vary, n12/n13 = warp XZ field1, n14/n15 = warp XZ field2
-- params: amplitude frequency warpAmp warpAmpY warpFrequency vScale widthVary widthFrequency yOffset
function bake(wx, wz, params, extLoS, s, bh)
    local amp = params.amplitude or 0
    if amp <= 0 then
        local result = {}
        for cy = 0, bh - 1 do result[cy + 1] = -1e9 end
        return result
    end

    local freq  = params.frequency      or 0.015
    local warpA = params.warpamp        or 0
    local warpF = params.warpfrequency  or 0.04
    local vsc   = params.vscale         or 0.35
    local wv    = params.widthvary      or 0
    local wvf   = params.widthfrequency or 0.003
    local yoff  = params.yoffset        or 0

    local addh = yoff ~= 0 and terrain_h(wx, wz) or 0

    local result = {}
    for cy = 0, bh - 1 do
        local wy  = extLoS + cy * s
        local sy  = (wy - addh + yoff) * vsc

        local awx1 = n12(wx * warpF, sy * warpF, wz * warpF) * warpA
        local awz1 = n13(wx * warpF, sy * warpF, wz * warpF) * warpA
        local awx2 = n14(wx * warpF, sy * warpF, wz * warpF) * warpA
        local awz2 = n15(wx * warpF, sy * warpF, wz * warpF) * warpA

        local na = n9 ((wx + awx1) * freq, sy * freq, (wz + awz1) * freq)
        local nb = n10((wx + awx2) * freq, sy * freq, (wz + awz2) * freq)
        local wn = n11(wx * wvf, sy * wvf, wz * wvf)

        local localWidth = math.max(amp * (1 + wv * wn), 0.001)
        local dist       = math.sqrt(na * na + nb * nb) * 0.7071
        local tunnelSDF  = (localWidth - dist) / localWidth * 4 * s

        result[cy + 1] = tunnelSDF
    end
    return result
end
