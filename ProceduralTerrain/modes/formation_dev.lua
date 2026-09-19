-- Writes its column into `out` (CLR float[], 0-based) instead of returning a table — avoids per-cell
-- MoonSharp table+DynValue garbage, which was the bake's dominant GC cost.
function bake(wx, wz, params, extLoS, s, bh, out)
    local warpx = n4(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp
    local warpz = n5(wx * params.warpfrequency, wz * params.warpfrequency, 0) * params.warpamp

    -- Smooth anchor: box average of terrain_h over smoothradius
    local sr = params.smoothradius or 0
    local raw_addh = terrain_h(wx + warpx, wz + warpz)
    local addh
    local ya = params.yanchor or 0
    if ya ~= 0 then
        addh = ya
    elseif sr > 0 then
        local sum = 0
        local step = sr
        local pts  = 0
        for dx = -1, 1 do
        for dz = -1, 1 do
            sum = sum + terrain_h(wx + dx * step + warpx, wz + dz * step + warpz)
            pts = pts + 1
        end end
        addh = sum / pts
    else
        addh = raw_addh
    end
    addh = addh - (params.yoffset or 0)

    -- Grad fade: finite difference, fade amplitude on steep terrain
    local gradfade = params.gradfade or 0
    local lamp = math.max(params.amplitude *
                    (1 + params.ampvary * n6(wx * params.ampfrequency, wz * params.ampfrequency, 0)), 0)
    if gradfade > 0 then
        -- grad magnitude is a standardized input injected by C# (ZoneGradCache) — no finite-diff.
        local grad_mag = params.grad_mag or 0
        local grad_factor = 1 - math.min(1, grad_mag / gradfade)
        lamp = lamp * grad_factor
    end

    local ts = 1 + params.twistvary * n7(wx * params.twistfrequency, wz * params.twistfrequency, 0)
    for cy = 0, bh - 1 do
        local wy   = extLoS + cy * s
        local rely = wy - addh
        local sy   = rely * params.vscale
        local ang  = wy * params.twist * ts
        local c, ss = math.cos(ang), math.sin(ang)
        local n    = n8((wx*c - wz*ss) * params.frequency, sy * params.frequency, (wx*ss + wz*c) * params.frequency)
        local ft   = math.max(0, math.min(1, rely / math.max(lamp, 0.001)))
        local efft = params.threshold - params.flare * (4 * ft * (1 - ft))
        out[cy] = rely - (n - efft) * lamp
    end
end
