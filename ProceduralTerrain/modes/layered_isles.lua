-- Sparse terraced islands ("layer cakes") with integrated canals, for an archipelago swamp.
-- blend = min : raises islands above the ambient swamp water. Everything is ONE coherent function of
-- position, so the canals follow the islands (they're carved from the same coordinates) instead of a
-- separate uniform tunnel pass.
--   * low-frequency MASK        -> sparse, sprinkled island placement (open water between)
--   * per-column top QUANTIZED   -> layer-cake terraces (softened by `terrace`)
--   * winding CHANNEL field      -> canals cut down through the island tops to the water line
--   * mid-height flare + 3D noise-> faces aren't dead-flat
-- params (lowercase): maskfreq islethresh islefade islandbase bluffheight layerstep terrace
--                     flare noiseamp noisefreq detailfreq warpamp warpfreq
--                     chanfreq chanwidth chancarve chanwarp chanwarpf
function bake(wx, wz, params, extLoS, s, bh, out)
    local sea        = 30
    local maskFreq   = params.maskfreq    or 0.0095
    local isleThresh = params.islethresh  or 0.12
    local isleFade   = params.islefade    or 0.12
    local islandBase = params.islandbase  or 2
    local bluff      = params.bluffheight or 24
    local layerStep  = params.layerstep   or 5
    local terrace    = params.terrace     or 0.6
    local flare      = params.flare       or 4
    local noiseAmp   = params.noiseamp    or 3
    local noiseFreq  = params.noisefreq   or 0.05
    local detailFreq = params.detailfreq  or 0.03
    local warpAmp    = params.warpamp     or 30
    local warpFreq   = params.warpfreq    or 0.01
    local chanFreq   = params.chanfreq    or 0.02
    local chanWidth  = params.chanwidth   or 0.35   -- larger = wider canals
    local chanCarve  = params.chancarve   or 0.9    -- 0..1 how fully canals cut to water
    local chanWarp   = params.chanwarp    or 25
    local chanWarpF  = params.chanwarpf   or 0.01

    -- Domain-warped mask so footprints aren't blobby-round.
    local wxw = wx + n3(wx * warpFreq, wz * warpFreq, 0) * warpAmp
    local wzw = wz + n4(wx * warpFreq, wz * warpFreq, 0) * warpAmp

    local mask = n1(wxw * maskFreq, wzw * maskFreq, 0)
    local m = (mask - isleThresh) / isleFade
    if m < 0 then m = 0 elseif m > 1 then m = 1 end
    m = m * m * (3 - 2 * m)

    if m <= 0 then
        for cy = 0, bh - 1 do out[cy] = 100000 end     -- no island -> ambient water
        return
    end

    local detail = n2(wx * detailFreq, wz * detailFreq, 0) * 0.5 + 0.5
    local rawTop = sea + islandBase + bluff * m * (0.5 + 0.5 * detail)
    local quant  = math.floor(rawTop / layerStep) * layerStep
    local top    = rawTop + (quant - rawTop) * terrace

    -- Winding canals: zero-crossings of a warped noise carve the top down to the water line.
    local cwx  = wx + n6(wx * chanWarpF, wz * chanWarpF, 0) * chanWarp
    local cwz  = wz + n7(wx * chanWarpF, wz * chanWarpF, 0) * chanWarp
    local cval = n5(cwx * chanFreq, cwz * chanFreq, 0)
    local chan = 1 - math.abs(cval) / chanWidth
    if chan < 0 then chan = 0 elseif chan > 1 then chan = 1 end
    chan = chan * chan * (3 - 2 * chan)
    top = top + ((sea - 2) - top) * (chan * chanCarve)   -- pull toward water in channels

    local hgt = math.max(top - sea, 1)
    for cy = 0, bh - 1 do
        local wy   = extLoS + cy * s
        local rely = wy - sea
        local ft   = rely / hgt
        if ft < 0 then ft = 0 elseif ft > 1 then ft = 1 end
        local bulge = flare * 4 * ft * (1 - ft)
        local n     = n8(wx * noiseFreq, rely * noiseFreq, wz * noiseFreq)
        local disp  = (bulge + n * noiseAmp) * m * (1 - chan)   -- no bulge inside canals
        out[cy] = (wy - top) - disp
    end
end
