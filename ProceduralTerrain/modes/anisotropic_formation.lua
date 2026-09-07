-- Anisotropic formation parity with C# ModeDDef.
-- Noise slot: n10 (pool index 9)
-- params: amplitude freqT freqN blurRadius alignment vScale gradFade seaFade
function bake(wx, wz, params, extLoS, s, bh)
    local amp        = params.amplitude  or 0
    if amp <= 0 then
        local result = {}
        for cy = 0, bh - 1 do result[cy + 1] = -1e9 end
        return result
    end

    local freqT      = params.freqt      or 0.02
    local freqN      = params.freqn      or 0.02
    local blurRadius = params.blurradius or 128
    local alignment  = params.alignment or 0
    local vsc        = params.vscale    or 0.1
    local gradFade   = params.gradfade   or 0
    local seaFade    = params.seafade    or 0

    local nmX = params.grad_dirx or 0
    local nmZ = params.grad_dirz or 0
    local mag  = params.grad_mag  or 0
    local blurH= params.grad_blurh or 0

    local gradFactor = gradFade > 0 and smoothstep(0, gradFade, mag) or 1
    local fT = lerp(freqT, freqN, alignment)
    local fN = lerp(freqN, freqT, alignment)
    local tCoord = (wx * (-nmZ) + wz * nmX) * fT * gradFactor
    local nCoord = (blurH / math.max(blurRadius, 1)) * fN * gradFactor

    local th = terrain_h(wx, wz)
    local seaFactor = seaFade > 0 and smoothstep(0, seaFade, th - 30) or 1
    local lAmp = amp * seaFactor

    local result = {}
    for cy = 0, bh - 1 do
        local wy  = extLoS + cy * s
        local mdn = n10(tCoord, wy * vsc, nCoord) * 0.5 + 0.5
        result[cy + 1] = (wy - th) - mdn * lAmp
    end
    return result
end
