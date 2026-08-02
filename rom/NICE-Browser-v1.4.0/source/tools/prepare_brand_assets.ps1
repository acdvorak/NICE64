param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot "..\\assets-source"),
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\generated-assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function Save-CroppedPng {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$CropX,
        [int]$CropY,
        [int]$CropWidth,
        [int]$CropHeight,
        [int]$Width,
        [int]$Height
    )

    $image = [System.Drawing.Image]::FromFile($Source)
    try {
        $bitmap = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $Width, $Height), $CropX, $CropY, $CropWidth, $CropHeight, [System.Drawing.GraphicsUnit]::Pixel)
                $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $graphics.Dispose() }
        }
        finally { $bitmap.Dispose() }
    }
    finally { $image.Dispose() }
}

function Save-TrimmedUiClickWave {
    param(
        [string]$Source,
        [string]$Destination
    )

    $sourceBytes = [System.IO.File]::ReadAllBytes($Source)
    $encoding = [System.Text.Encoding]::ASCII
    $cursor = 12
    $formatOffset = -1
    $dataOffset = -1
    $dataLength = 0
    while ($cursor + 8 -le $sourceBytes.Length) {
        $chunkId = $encoding.GetString($sourceBytes, $cursor, 4)
        $chunkLength = [BitConverter]::ToUInt32($sourceBytes, $cursor + 4)
        if ($chunkId -eq "fmt ") { $formatOffset = $cursor + 8 }
        if ($chunkId -eq "data") {
            $dataOffset = $cursor + 8
            $dataLength = $chunkLength
            break
        }
        $cursor += 8 + $chunkLength + ($chunkLength % 2)
    }
    if ($formatOffset -lt 0 -or $dataOffset -lt 0) { throw "Keyboard.wav is missing PCM data." }

    $format = [BitConverter]::ToUInt16($sourceBytes, $formatOffset)
    $channels = [BitConverter]::ToUInt16($sourceBytes, $formatOffset + 2)
    $sampleRate = [BitConverter]::ToUInt32($sourceBytes, $formatOffset + 4)
    $bits = [BitConverter]::ToUInt16($sourceBytes, $formatOffset + 14)
    if ($format -ne 1 -or $bits -ne 16 -or $channels -lt 1) { throw "Keyboard.wav must be 16-bit PCM." }

    $frameBytes = $channels * 2
    $frameCount = [int]($dataLength / $frameBytes)
    $outputFrames = [Math]::Min($frameCount, [int]($sampleRate / 20)) # 50 ms
    $peak = 1
    for ($frame = 0; $frame -lt $outputFrames; $frame++) {
        $sum = 0
        for ($channel = 0; $channel -lt $channels; $channel++) {
            $sum += [BitConverter]::ToInt16($sourceBytes, $dataOffset + $frame * $frameBytes + $channel * 2)
        }
        $peak = [Math]::Max($peak, [Math]::Abs([int]($sum / $channels)))
    }
    $gain = [Math]::Min(1.0, 23500.0 / $peak)
    $fadeFrames = [Math]::Max(1, [int]($sampleRate / 200)) # final 5 ms
    $outputData = New-Object byte[] ($outputFrames * 2)
    for ($frame = 0; $frame -lt $outputFrames; $frame++) {
        $sum = 0
        for ($channel = 0; $channel -lt $channels; $channel++) {
            $sum += [BitConverter]::ToInt16($sourceBytes, $dataOffset + $frame * $frameBytes + $channel * 2)
        }
        $sample = ($sum / $channels) * $gain
        if ($frame -ge $outputFrames - $fadeFrames) {
            $sample *= [double]($outputFrames - $frame) / $fadeFrames
        }
        $sample = [Math]::Max(-32768, [Math]::Min(32767, [Math]::Round($sample)))
        [BitConverter]::GetBytes([Int16]$sample).CopyTo($outputData, $frame * 2)
    }

    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Open($Destination, [System.IO.FileMode]::Create))
    try {
        $writer.Write($encoding.GetBytes("RIFF"))
        $writer.Write([UInt32](36 + $outputData.Length))
        $writer.Write($encoding.GetBytes("WAVEfmt "))
        $writer.Write([UInt32]16); $writer.Write([UInt16]1); $writer.Write([UInt16]1)
        $writer.Write([UInt32]$sampleRate); $writer.Write([UInt32]($sampleRate * 2))
        $writer.Write([UInt16]2); $writer.Write([UInt16]16)
        $writer.Write($encoding.GetBytes("data")); $writer.Write([UInt32]$outputData.Length); $writer.Write($outputData)
    }
    finally { $writer.Dispose() }
}

$pngRoot = Join-Path $OutputRoot "png"
$audioRoot = Join-Path $OutputRoot "audio"
New-Item -ItemType Directory -Force -Path $pngRoot, $audioRoot | Out-Null

Save-CroppedPng (Join-Path $SourceRoot "NICE 64 Logo.png") (Join-Path $pngRoot "nice64_appbar.png") 0 8 310 94 112 34
Save-CroppedPng (Join-Path $SourceRoot "NICE 64 Logo.png") (Join-Path $pngRoot "nice64_intro.png") 0 0 310 148 248 118
Save-CroppedPng (Join-Path $SourceRoot "Banner.png") (Join-Path $pngRoot "nice64_banner.png") 0 0 734 118 240 39
Save-CroppedPng (Join-Path $SourceRoot "Melfegor.png") (Join-Path $pngRoot "melfegor_intro.png") 0 0 1254 1254 192 192
# Home content uses the center band so the globe remains visible in the N64 viewport.
Save-CroppedPng (Join-Path $SourceRoot "BrowserHomeBackground.png") (Join-Path $pngRoot "browser_home_background.png") 0 208 1448 670 320 148

Copy-Item -LiteralPath (Join-Path $SourceRoot "Intro.wav") -Destination (Join-Path $audioRoot "intro.wav") -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot "Error.wav") -Destination (Join-Path $audioRoot "error.wav") -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot "Keyboard.wav") -Destination (Join-Path $audioRoot "keyboard.wav") -Force
Save-TrimmedUiClickWave (Join-Path $SourceRoot "Keyboard.wav") (Join-Path $audioRoot "keyboard_click.wav")

Get-ChildItem -LiteralPath $OutputRoot -Recurse -File | Sort-Object FullName | Select-Object Name, Length, FullName
