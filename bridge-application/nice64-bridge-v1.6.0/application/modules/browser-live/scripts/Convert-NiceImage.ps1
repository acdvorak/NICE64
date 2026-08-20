param(
    [Parameter(Mandatory=$true)][string]$Url,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet("FIT", "FILL")][string]$Mode = "FIT"
)

$ErrorActionPreference = "Stop"
$sourcePath = "$OutputPath.source"
$ImageMaxWidth = 160
$ImageMaxHeight = 96

function ConvertTo-Rgb5A1([System.Drawing.Color]$Pixel) {
    [int]$r5 = ([int]$Pixel.R) -shr 3
    [int]$g5 = ([int]$Pixel.G) -shr 3
    [int]$b5 = ([int]$Pixel.B) -shr 3
    [int]$a1 = if ($Pixel.A -ge 128) { 1 } else { 0 }
    return (($r5 -shl 11) -bor ($g5 -shl 6) -bor ($b5 -shl 1) -bor $a1)
}

function Test-Rgb5A1Packing {
    $samples = @(
        @{ Name = "red";   Color = [System.Drawing.Color]::FromArgb(255, 255, 0, 0); Expected = 0xF801 },
        @{ Name = "green"; Color = [System.Drawing.Color]::FromArgb(255, 0, 255, 0); Expected = 0x07C1 },
        @{ Name = "blue";  Color = [System.Drawing.Color]::FromArgb(255, 0, 0, 255); Expected = 0x003F },
        @{ Name = "white"; Color = [System.Drawing.Color]::FromArgb(255, 255, 255, 255); Expected = 0xFFFF },
        @{ Name = "black"; Color = [System.Drawing.Color]::FromArgb(255, 0, 0, 0); Expected = 0x0001 },
        @{ Name = "milk";  Color = [System.Drawing.Color]::FromArgb(255, 241, 236, 240); Expected = 0xF77D }
    )

    foreach ($sample in $samples) {
        [int]$actual = ConvertTo-Rgb5A1 $sample.Color
        if ($actual -ne [int]$sample.Expected) { throw "RGB5A1_SELF_CHECK_FAILED:$($sample.Name):$('{0:X4}' -f $actual)" }
        if ($env:NICE64_IMAGE_CONVERTER_DIAGNOSTICS -eq "1") {
            $pixel = $sample.Color
            Write-Host ("RGB5A1 {0}: source={1},{2},{3},{4} r5={5} g5={6} b5={7} expected=0x{8:X4} actual=0x{9:X4}" -f $sample.Name, $pixel.R, $pixel.G, $pixel.B, $pixel.A, (([int]$pixel.R) -shr 3), (([int]$pixel.G) -shr 3), (([int]$pixel.B) -shr 3), [int]$sample.Expected, $actual)
        }
    }
}

try {
    Add-Type -AssemblyName System.Drawing
    Test-Rgb5A1Packing
    $request = [System.Net.WebRequest]::Create($Url)
    $request.Timeout = 8000
    $response = $request.GetResponse()
    if ($response.ContentLength -gt 2097152) { throw "IMAGE_TOO_LARGE" }
    $stream = $response.GetResponseStream()
    $file = [System.IO.File]::Create($sourcePath)
    $stream.CopyTo($file); $file.Dispose(); $stream.Dispose(); $response.Dispose()
    if ((Get-Item $sourcePath).Length -gt 2097152) { throw "IMAGE_TOO_LARGE" }
    $source = [System.Drawing.Image]::FromFile($sourcePath)
    if ($source.Width -lt 48 -or $source.Height -lt 32) { throw "IMAGE_TOO_SMALL" }
    if ($Mode -eq "FILL") {
        # Scale proportionally until the 160x96 target is covered, then center-crop.
        $scale = [Math]::Max($ImageMaxWidth / [double]$source.Width, $ImageMaxHeight / [double]$source.Height)
        $scaledWidth = [Math]::Max($ImageMaxWidth, [Math]::Round($source.Width * $scale, 0, [System.MidpointRounding]::AwayFromZero))
        $scaledHeight = [Math]::Max($ImageMaxHeight, [Math]::Round($source.Height * $scale, 0, [System.MidpointRounding]::AwayFromZero))
        $cropX = [Math]::Floor(($scaledWidth - $ImageMaxWidth) / 2)
        $cropY = [Math]::Floor(($scaledHeight - $ImageMaxHeight) / 2)
        $sourceCropWidth = [Math]::Min($source.Width, [Math]::Max(1, [Math]::Round($ImageMaxWidth / $scale, 0, [System.MidpointRounding]::AwayFromZero)))
        $sourceCropHeight = [Math]::Min($source.Height, [Math]::Max(1, [Math]::Round($ImageMaxHeight / $scale, 0, [System.MidpointRounding]::AwayFromZero)))
        $sourceCropX = [Math]::Max(0, [Math]::Floor(($source.Width - $sourceCropWidth) / 2))
        $sourceCropY = [Math]::Max(0, [Math]::Floor(($source.Height - $sourceCropHeight) / 2))
        $width = $ImageMaxWidth; $height = $ImageMaxHeight
    } else {
        # FIT preserves the full image, never upscales, and never crops.
        $scale = [Math]::Min(1.0, [Math]::Min($ImageMaxWidth / [double]$source.Width, $ImageMaxHeight / [double]$source.Height))
        $width = [Math]::Min($ImageMaxWidth, [Math]::Max(1, [Math]::Round($source.Width * $scale, 0, [System.MidpointRounding]::AwayFromZero)))
        $height = [Math]::Min($ImageMaxHeight, [Math]::Max(1, [Math]::Round($source.Height * $scale, 0, [System.MidpointRounding]::AwayFromZero)))
        $scaledWidth = $width; $scaledHeight = $height; $cropX = 0; $cropY = 0
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
    if ($Mode -eq "FILL") {
        $destination = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $graphics.DrawImage($source, $destination, $sourceCropX, $sourceCropY, $sourceCropWidth, $sourceCropHeight, [System.Drawing.GraphicsUnit]::Pixel)
    } else {
        $graphics.DrawImage($source, 0, 0, $width, $height)
    }
    if ($env:NICE64_IMAGE_CONVERTER_DIAGNOSTICS -eq "1") {
        Write-Host ("NIMG image mode={0} source={1}x{2} scaled={3}x{4} crop={5},{6} final={7}x{8}" -f $Mode, $source.Width, $source.Height, $scaledWidth, $scaledHeight, $cropX, $cropY, $width, $height)
    }
    $graphics.Dispose(); $source.Dispose()
    $directory = Split-Path -Parent $OutputPath; New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $writer = New-Object System.IO.BinaryWriter([System.IO.File]::Create($OutputPath))
    function Write-BE16([int]$v) { $writer.Write([byte](($v -shr 8) -band 255)); $writer.Write([byte]($v -band 255)) }
    function Write-BE32([int]$v) { $writer.Write([byte](($v -shr 24) -band 255)); $writer.Write([byte](($v -shr 16) -band 255)); $writer.Write([byte](($v -shr 8) -band 255)); $writer.Write([byte]($v -band 255)) }
    $writer.Write([Text.Encoding]::ASCII.GetBytes("NIMG")); Write-BE16 1; Write-BE16 $width; Write-BE16 $height; Write-BE16 1; Write-BE32 ($width * $height * 2)
    for ($y=0; $y -lt $height; $y++) { for ($x=0; $x -lt $width; $x++) { $p=$bitmap.GetPixel($x,$y); [int]$v=ConvertTo-Rgb5A1 $p; if ($env:NICE64_IMAGE_CONVERTER_DIAGNOSTICS -eq "1" -and $x -eq 20 -and $y -eq 20) { Write-Host ("RGB5A1 bitmap sample: source={0},{1},{2},{3} packed=0x{4:X4}" -f $p.R, $p.G, $p.B, $p.A, $v) }; Write-BE16 $v } }
    $writer.Dispose(); $bitmap.Dispose(); exit 0
} catch { if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }; exit 2 }
finally { if (Test-Path $sourcePath) { Remove-Item $sourcePath -Force } }
