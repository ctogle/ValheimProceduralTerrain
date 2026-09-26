-- Gabor-noise surface features oriented by the local gradient frame (contour=0 -> radial ridges
-- down the slope; contour=1 -> horizontal elevation rings). Height and frame are STANDARDIZED
-- INPUTS injected by C# (terrain_h0 + grad_dirx/dirz/mag from ZoneGradCache) — this mode never
-- calls terrain_h; it only consumes the input struct.
-- params: featureAmp gaborCell gaborFreq gaborRadius gaborAniso contour slopeFade
--         ringFreq ringWarp ringWarpF extendUp
function bake(wx, wz, params, extLoS, s, bh)
    -- Feature strength: use the index-0 amplitude (coverage-faded by C# — totW-normalized in param
    -- mode, ampB*coverageFrac in sdf) so one-sided modes taper out over blendRadius into neighbors
    -- instead of hard-cutting. featureAmp kept only as a fallback for configs that don't set amplitude.
    local strength    = params.amplitude   or params.featureamp or 10
    local gaborCell   = params.gaborcell   or 40    -- kernel spacing (world units); ~ feature size
    local gaborFreq   = params.gaborfreq   or 0.025 -- across-slope wave cycles per world unit
    local gaborRadius = params.gaborradius or 45    -- kernel envelope radius (world units)
    local gaborAniso  = params.gaboraniso  or 3     -- envelope elongation along the feature direction
    local contour     = (params.contour or 0) > 0.5 -- 0 = radial ridges (down-slope); 1 = contour rings
    local slopeFade   = params.slopefade   or 0.4   -- gradient magnitude at which features reach full amp
    local ringFreq    = params.ringfreq    or 0.08  -- rings per meter of elevation (contour=1)
    local ringWarp    = params.ringwarp    or 3     -- elevation noise (m) for organic ring edges
    local ringWarpF   = params.ringwarpf   or 0.01  -- horizontal frequency of the warp

    -- Standardized inputs from C# (no per-column terrain_h recompute).
    local h0   = params.terrain_h0 or 0
    local ghx  = params.grad_dirx  or 0
    local ghz  = params.grad_dirz  or 0
    local gmag = params.grad_mag   or 0
    if gmag <= 1e-4 then ghx, ghz = 1, 0 end
    local thx, thz = -ghz, ghx   -- tangent (across slope)

    -- Deterministic hash -> [0,1)
    local function hash(a, b)
        local n = math.sin(a * 127.1 + b * 311.7) * 43758.5453
        return n - math.floor(n)
    end

    local feat
    if contour then
        -- Horizontal contour rings: a function of elevation, exactly level (independent of the
        -- gradient direction). Warp the elevation slightly for organic, wavy ring edges.
        local hw = h0 + ringWarp * n1(wx * ringWarpF, wz * ringWarpF, 0)
        feat = math.cos(6.2831853 * ringFreq * hw)
    else
        -- Gabor sum over nearby kernel cells (radial ridges): wave across-slope (dt), envelope
        -- elongated down-slope (dg).
        local invR2 = 1 / (gaborRadius * gaborRadius)
        local ci0 = math.floor(wx / gaborCell)
        local cj0 = math.floor(wz / gaborCell)
        local sum, norm = 0, 0
        for di = -2, 2 do
            for dj = -2, 2 do
                local ci, cj = ci0 + di, cj0 + dj
                local jx = (ci + hash(ci, cj)) * gaborCell
                local jz = (cj + hash(cj + 7.3, ci - 2.1)) * gaborCell
                local dx, dz = wx - jx, wz - jz
                local dt = dx * thx + dz * thz         -- across slope (tangent)
                local dg = dx * ghx + dz * ghz         -- down slope (gradient)
                local rr = (dt*dt + (dg*dg) / (gaborAniso*gaborAniso)) * invR2
                if rr < 6 then
                    local env   = math.exp(-math.pi * rr)
                    local phase = hash(ci * 1.3 + cj, cj * 1.7 - ci) * 6.2831853
                    local wave  = math.cos(6.2831853 * gaborFreq * dt + phase)
                    sum  = sum  + env * wave
                    norm = norm + env
                end
            end
        end
        feat = norm > 1e-4 and sum / math.sqrt(norm) or 0
    end

    -- blend=min only raises, so shift features additive (ridges up, troughs at the existing surface).
    feat = feat * 0.5 + 0.5

    -- Slope-magnitude amplitude: flats/summits smooth, slopes full.
    local amp = 1
    if slopeFade > 0 then
        local t = gmag / slopeFade
        if t < 0 then t = 0 elseif t > 1 then t = 1 end
        amp = t * t * (3 - 2 * t)
    end

    local surf_h = h0 + strength * feat * amp

    local result = {}
    for cy = 0, bh - 1 do
        local wy = extLoS + cy * s
        result[cy + 1] = wy - surf_h
    end
    return result
end
