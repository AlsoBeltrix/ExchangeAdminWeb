Add-Type -AssemblyName System.Drawing

# Build a multi-image .ico by hand: System.Drawing can READ .ico but cannot author a multi-size one,
# and Icon.Save writes a single frame. The format is small enough to emit directly.
#
# Each entry is a whole PNG file (Vista+ PNG-compressed icon, universally supported for years), so
# the payload is just the file bytes.
#
# Sizes 16 and 32 use the SIMPLIFIED glyph; 48 and up use the full card. The whole point of the
# format is a different drawing per size.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "eaw-favicon"

$entries = @(
    @{ Size = 16;  File = "$work\favicon-small-16.png" },
    @{ Size = 32;  File = "$work\favicon-small-32.png" },
    @{ Size = 48;  File = "$work\favicon-small-48.png" },
    @{ Size = 64;  File = "$work\favicon-64.png" },
    @{ Size = 256; File = "$work\favicon-256.png" }
)

foreach ($e in $entries) {
    if (-not (Test-Path $e.File)) {
        throw "missing source image: $($e.File). Run make-favicon.ps1 and make-favicon-small.ps1 first."
    }
    $e.Bytes = [System.IO.File]::ReadAllBytes($e.File)
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICONDIR
$bw.Write([uint16]0)                  # reserved
$bw.Write([uint16]1)                  # type: 1 = icon
$bw.Write([uint16]$entries.Count)

# ICONDIRENTRY x N. Offsets follow the whole directory.
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    # 256 is encoded as 0 in a single byte - the format's one wart.
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim)             # width
    $bw.Write([byte]$dim)             # height
    $bw.Write([byte]0)                # palette count (0 = truecolour)
    $bw.Write([byte]0)                # reserved
    $bw.Write([uint16]1)              # colour planes
    $bw.Write([uint16]32)             # bits per pixel
    $bw.Write([uint32]$e.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}

foreach ($e in $entries) { $bw.Write($e.Bytes) }

$bw.Flush()
$icoPath = "$work\favicon.ico"
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

# Read it back through the framework as an independent check that the directory is well-formed.
$verify = New-Object System.Drawing.Icon($icoPath)
Write-Output ("ico written: {0} bytes, {1} entries, default frame {2}x{3}" -f `
    (Get-Item $icoPath).Length, $entries.Count, $verify.Width, $verify.Height)
$verify.Dispose()

# Install the shipped set. These three are the only outputs that leave the work directory.
$wwwroot = Join-Path (Split-Path $PSScriptRoot -Parent) 'wwwroot'
Copy-Item $icoPath              (Join-Path $wwwroot 'favicon.ico')          -Force
Copy-Item "$work\favicon-256.png" (Join-Path $wwwroot 'favicon.png')        -Force
Copy-Item "$work\favicon-180.png" (Join-Path $wwwroot 'apple-touch-icon.png') -Force
Write-Output "installed favicon.ico, favicon.png, apple-touch-icon.png into wwwroot"
