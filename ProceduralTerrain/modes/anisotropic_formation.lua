-- Anisotropic formation: noise field stretched along terrain tangent/normal.
-- Gradient injected by C# ZoneGradCache (bilinear interpolation of zone-center grads — continuous).
-- params (cfg): amplitude freqT freqN blurRadius alignment vScale gradFade seaFade
-- params (injected): grad_dirx grad_dirz grad_mag
function bake(wx, wz, params, extLoS, s, bh)
    local amp = params.amplitude or 0
    if amp <= 0 then
        local result = {}
        for cy = 0, bh - 1 do result[cy + 1] = -1e9 end
        return result
    end

    local freqT    = params.freqt     or 0.001
    local freqN    = params.freqn     or 0.005
    local align    = params.alignment or 0
    local vsc      = params.vscale    or 0.1
    local gradFade = params.gradfade  or 0
    local seaFade  = params.seafade   or 0

    local nmX = params.grad_dirx or 0
    local nmZ = params.grad_dirz or 0
    local mag  = params.grad_mag  or 0
    if not _logged then _logged = true; log(string.format("aniso wx=%.0f wz=%.0f dirx=%.3f dirz=%.3f mag=%.4f", wx, wz, nmX, nmZ, mag)) end

    local gradFactor = gradFade > 0 and smoothstep(0, gradFade, mag) or 1
    local fT = lerp(freqT, freqN, align) * gradFactor
    local fN = lerp(freqN, freqT, align) * gradFactor

    local tx, tz  = -nmZ, nmX
    local tCoord  = wx * tx + wz * tz
    local nCoord  = wx * nmX + wz * nmZ

    local th = terrain_h(wx, wz)
    local seaFactor = seaFade > 0 and smoothstep(0, seaFade, th - 30) or 1
    local lAmp = amp * seaFactor

    local result = {}
    for cy = 0, bh - 1 do
        local wy = extLoS + cy * s
        local mdn = n10(tCoord * fT, nCoord * fN, wy * vsc) * 0.5 + 0.5
        result[cy + 1] = (wy - th) - mdn * lAmp
    end
    return result
end
