# Builds a macOS .icns from a square PNG.
#
# An .icns is a flat container: the magic 'icns', the total length, then one entry per size —
# 4-byte OSType + 4-byte length (including its own 8-byte header) + the payload. Since macOS 10.7 the
# payload may be a PNG as-is, so no Apple tooling is needed to produce one.
#
# Both integers are BIG-endian, which is the easy thing to get wrong on a little-endian host.

param(
    [string]$Source = "C:\Privat - MIKROTIK\src\TikMan.App.Avalonia\Assets\tikman.png",
    [string]$Target = "C:\Privat - MIKROTIK\packaging\macos\tikman.icns"
)

Add-Type -AssemblyName System.Drawing

# OSType -> pixel size. These are the PNG-capable types; together they cover every slot the Finder,
# the Dock and Get Info ask for, at 1x and 2x.
$types = [ordered]@{
    'icp4' = 16
    'icp5' = 32
    'ic11' = 32     # 16x16@2x
    'ic12' = 64     # 32x32@2x
    'ic07' = 128
    'ic13' = 256    # 128x128@2x
    'ic08' = 256
    'ic14' = 512    # 256x256@2x
    'ic09' = 512
    'ic10' = 1024   # 512x512@2x
}

$src = [System.Drawing.Image]::FromFile($Source)
if ($src.Width -ne $src.Height) { throw "source icon must be square, got $($src.Width)x$($src.Height)" }
"source: $($src.Width)x$($src.Height)"

$entries = New-Object System.Collections.Generic.List[byte[]]

foreach ($type in $types.Keys) {
    $size = $types[$type]

    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $png = $ms.ToArray()
    $ms.Dispose()

    # entry = OSType + big-endian length (payload + this 8-byte header) + payload
    $len = [System.BitConverter]::GetBytes([int]($png.Length + 8))
    [Array]::Reverse($len)

    $entry = New-Object System.Collections.Generic.List[byte]
    $entry.AddRange([System.Text.Encoding]::ASCII.GetBytes($type))
    $entry.AddRange($len)
    $entry.AddRange($png)
    $entries.Add($entry.ToArray())

    "  {0}  {1,4}x{1,-4}  {2,7:N0} B" -f $type, $size, $png.Length
}

$src.Dispose()

$payloadLength = ($entries | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum
$total = [System.BitConverter]::GetBytes([int]($payloadLength + 8))
[Array]::Reverse($total)

$out = New-Object System.Collections.Generic.List[byte]
$out.AddRange([System.Text.Encoding]::ASCII.GetBytes('icns'))
$out.AddRange($total)
foreach ($e in $entries) { $out.AddRange($e) }

$dir = Split-Path $Target -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Target, $out.ToArray())

"written: $Target  ($($out.Count) bytes, $($entries.Count) sizes)"

# Read it back and walk the entries – a malformed length field is silently accepted by the writer but
# makes the Finder fall back to a blank document icon, which is exactly the failure we would not notice.
$bytes = [System.IO.File]::ReadAllBytes($Target)
if ([System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'icns') { throw "bad magic" }
$declared = [System.BitConverter]::ToInt32(($bytes[4..7] | ForEach-Object { $_ })[3..0], 0)
$hdr = $bytes[4..7]; [Array]::Reverse($hdr)
$declared = [System.BitConverter]::ToInt32($hdr, 0)
if ($declared -ne $bytes.Length) { throw "declared length $declared != actual $($bytes.Length)" }

$pos = 8; $count = 0
while ($pos -lt $bytes.Length) {
    $t = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 4)
    $l = $bytes[($pos + 4)..($pos + 7)]; [Array]::Reverse($l)
    $entryLen = [System.BitConverter]::ToInt32($l, 0)
    if ($entryLen -lt 8) { throw "entry $t has bogus length $entryLen" }
    $pos += $entryLen
    $count++
}
if ($pos -ne $bytes.Length) { throw "entries overrun the file: ended at $pos of $($bytes.Length)" }
"verified: $count entries, lengths consistent"
