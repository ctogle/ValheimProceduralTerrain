-- Tunnel network mode (equivalent to type A)
-- Noise indices: n1=caveNoise, n2=caveNoise2, n3=widthNoise
function bake(wx, wz, params, extLoS, s, bh)
    local f  = params.frequency
    local vs = params.vscale
    local wf = params.widthfrequency
    local wn = n3(wx * wf, 0, wz * wf)
    local w  = math.max(params.amplitude * (1 + params.widthvary * wn), 0.001)
    local result = {}
    for cy = 0, bh - 1 do
        local wy   = extLoS + cy * s
        local na   = n1(wx * f, wy * vs, wz * f)
        local nb   = n2(wx * f, wy * vs, wz * f)
        local dist = math.sqrt(na*na + nb*nb) * 0.7071
        result[cy + 1] = (w - dist) / w * 4
    end
    return result
end
