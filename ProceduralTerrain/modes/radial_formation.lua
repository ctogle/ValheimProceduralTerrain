-- Gabor-noise surface features, oriented by the local gradient frame.
-- Sum of sparse oriented kernels (Gaussian envelope * cosine wave), each in WORLD units:
--   * wave oscillates across-slope (along tangent) -> ridges run DOWN the slope
--   * envelope elongated along-slope (gradient)     -> features stretch down the slope
-- Translation-invariant (grid+hash), constant world scale, orientation from the frame,
-- no polar/angle degeneracy. Testbed base = two overlapping rounded cones.
--
-- params: coneX coneZ conePeak coneSlope tipRound  cone2X cone2Z cone2Peak
--         featureAmp gaborCell gaborFreq gaborRadius gaborAniso slopeFade gradEps extendUp
function bake(wx, wz, params, extLoS, s, bh)
    local coneX     = params.conex     or 0
    local coneZ     = params.conez     or 0
    local conePeak  = params.conepeak  or 240
    local coneSlope = params.coneslope or 1.0
    local tipRound  = params.tipround  or 30
    local cone2X    = params.cone2x    or 250
    local cone2Z    = params.cone2z    or 0
    local cone2Peak = params.cone2peak or 160

    local featureAmp  = params.featureamp  or 10
    local gaborCell   = params.gaborcell   or 40    -- kernel spacing (world units); ~ feature size
    local gaborFreq   = params.gaborfreq   or 0.025 -- across-slope wave cycles per world unit
    local gaborRadius = params.gaborradius or 45    -- kernel envelope radius (world units)
    local gaborAniso  = params.gaboraniso  or 3     -- envelope elongation along the feature direction
    local contour     = (params.contour or 0) > 0.5 -- 0 = radial ridges (down-slope); 1 = contour rings
    local slopeFade   = params.slopefade   or 0.4   -- gradient magnitude at which features reach full amp
    local gradEps     = params.gradeps     or 20    -- gradient sample radius; larger = smoother frame
    local useTerrain  = (params.useterrain or 1) > 0.5 -- 1 = real EW terrain; 0 = two-cone testbed
    -- Contour-ring params (contour=1): rings are level sets of elevation -> perfectly horizontal.
    local ringFreq    = params.ringfreq     or 0.08  -- rings per meter of elevation
    local ringWarp    = params.ringwarp     or 3     -- elevation noise (m) for organic ring edges
    local ringWarpF   = params.ringwarpf    or 0.01  -- horizontal frequency of the warp

    -- Rounded cone height (smooth apex, straight cone far out).
    local function coneH(x, z, cx, cz, peak)
        local dx, dz = x - cx, z - cz
        local d = math.sqrt(dx*dx + dz*dz)
        return peak - coneSlope * (math.sqrt(d*d + tipRound*tipRound) - tipRound)
    end
    local function base_h(x, z)
        if useTerrain then return terrain_h(x, z) end
        local a = coneH(x, z, coneX,  coneZ,  conePeak)
        local b = coneH(x, z, cone2X, cone2Z, cone2Peak)
        return a > b and a or b
    end

    -- Deterministic hash -> [0,1)
    local function hash(a, b)
        local n = math.sin(a * 127.1 + b * 311.7) * 43758.5453
        return n - math.floor(n)
    end

    local h0 = base_h(wx, wz)

    -- Smoothed frame from finite-difference gradient.
    local e  = gradEps
    local gx = (base_h(wx + e, wz) - base_h(wx - e, wz)) / (2 * e)
    local gz = (base_h(wx, wz + e) - base_h(wx, wz - e)) / (2 * e)
    local gmag = math.sqrt(gx*gx + gz*gz)
    local ghx, ghz
    if gmag > 1e-4 then ghx, ghz = gx / gmag, gz / gmag
    else                ghx, ghz = 1, 0 end
    local thx, thz = -ghz, ghx   -- tangent (across slope)

    local feat
    if contour then
        -- Horizontal contour rings: purely a function of elevation, so they're exactly level
        -- (no dependence on the jittery gradient direction). Warp the elevation slightly for
        -- organic, wavy ring edges.
        local hw = h0 + ringWarp * n1(wx * ringWarpF, wz * ringWarpF, 0)
        feat = math.cos(6.2831853 * ringFreq * hw)
    else
    -- Gabor sum over nearby kernel cells (radial ridges).
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
            local dt = dx * thx + dz * thz             -- across slope (tangent / contour dir)
            local dg = dx * ghx + dz * ghz             -- down slope (gradient dir)
            -- Radial ridges: wave across-slope (dt), envelope long down-slope (dg).
            -- Contour bands: wave down-slope (dg), envelope long across-slope (dt).
            local waveC, across
            if contour then waveC, across = dg, dt
            else            waveC, across = dt, dg end
            local rr = (waveC*waveC + (across*across) / (gaborAniso*gaborAniso)) * invR2
            if rr < 6 then
                local env   = math.exp(-math.pi * rr)
                local phase = hash(ci * 1.3 + cj, cj * 1.7 - ci) * 6.2831853
                local wave  = math.cos(6.2831853 * gaborFreq * waveC + phase)
                sum  = sum  + env * wave
                norm = norm + env
            end
        end
    end
    feat = norm > 1e-4 and sum / math.sqrt(norm) or 0
    end

    -- On real terrain, blend=min only raises, so shift features additive (ridges up, troughs
    -- at the existing surface) to avoid clipping the negative half.
    if useTerrain then feat = feat * 0.5 + 0.5 end

    -- Slope-magnitude amplitude: flats/summits smooth, slopes full.
    local amp = 1
    if slopeFade > 0 then
        local t = gmag / slopeFade
        if t < 0 then t = 0 elseif t > 1 then t = 1 end
        amp = t * t * (3 - 2 * t)
    end

    local surf_h = h0 + featureAmp * feat * amp

    local result = {}
    for cy = 0, bh - 1 do
        local wy = extLoS + cy * s
        result[cy + 1] = wy - surf_h
    end
    return result
end
