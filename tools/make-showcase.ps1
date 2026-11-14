# Renders the README images and the animated preview.
# Run from the repo root after building:  powershell -ExecutionPolicy Bypass -File tools\make-showcase.ps1
# Needs ffmpeg on PATH for the GIF/MP4.
$root  = Split-Path $PSScriptRoot -Parent
$media = Join-Path $root "docs\media"
$exe   = Join-Path $root "dist\GlassPlayer.exe"

$env:GLASSPLAYER_SHOWCASE = $media
Start-Process $exe -Wait
Remove-Item Env:GLASSPLAYER_SHOWCASE

$frames = Join-Path $media "frames"
if (-not (Test-Path "$frames\frames.txt")) { Write-Error "No frames rendered (see $media\error.txt)"; exit 1 }

Push-Location $frames
ffmpeg -v error -y -f concat -safe 0 -i frames.txt -vf "fps=30,scale=600:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a" ..\preview.gif
ffmpeg -v error -y -f concat -safe 0 -i frames.txt -vf "fps=60,scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p" -c:v libx264 -crf 18 -movflags +faststart ..\preview.mp4
Pop-Location
Remove-Item $frames -Recurse -Force
Get-ChildItem $media | Select-Object Name, @{n='KB';e={[int]($_.Length/1KB)}}
