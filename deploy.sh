#!/bin/bash
set -e
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
SRC_WSL="/mnt/c/dev/VoxCavesMod/VoxCaves/bin/Release/netstandard2.1/ProceduralTerrain.dll"
DST="/mnt/c/Users/curti/AppData/Roaming/r2modmanPlus-local/Valheim/profiles/onepoint0/BepInEx/plugins/ctogle-ProceduralTerrain/ProceduralTerrain.dll"

"$DOTNET" build "C:/dev/VoxCavesMod/VoxCaves/VoxCaves.csproj" -c Release

mkdir -p "$(dirname "$DST")"
cp "$SRC_WSL" "$DST"
echo "Done. Relaunch Valheim to load the new DLL."
