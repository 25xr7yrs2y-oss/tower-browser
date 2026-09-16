[CmdletBinding()]
param([string]$ReleaseDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "release"))

$ErrorActionPreference = "Stop"
$version = "1.0.9"
$base = "PrivacyBrowser-$version-windows-x64-portable"
$zip = Join-Path $ReleaseDirectory "$base.zip"
$source = Join-Path $ReleaseDirectory "PrivacyBrowser-$version-myst-lmprove-source-7944a4c.tar.gz"
$checksums = Join-Path $ReleaseDirectory "PrivacyBrowser-$version-SHA256SUMS.txt"

foreach ($path in @($zip, $source, $checksums)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release artifact missing: $path" }
    if ((Get-Item -LiteralPath $path).Length -eq 0) { throw "Release artifact is empty: $path" }
}
if ((Get-Item -LiteralPath $zip).Length -lt 100MB) { throw "Portable release is unexpectedly small and may omit dependencies." }
if ((Get-Item -LiteralPath $source).Length -lt 1MB) { throw "Corresponding source archive is unexpectedly small." }

$expectedLines = Get-Content -LiteralPath $checksums
foreach ($path in @($zip, $source)) {
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = [IO.Path]::GetFileName($path)
    if ("$actual  $name" -notin $expectedLines) { throw "Checksum file does not match $name." }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("privacy-browser-verify-" + [Guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $temp -Force
    $root = Join-Path $temp $base
    $exe = Join-Path $root "PrivacyBrowser.exe"
    $browser = Join-Path $root "vendor\mullvad-browser\mullvadbrowser.exe"
    $backend = Join-Path $root "vendor\myst-lmprove\resources\app.asar.unpacked\node_modules\@mysteriumnetwork\node\bin\win\x64\myst.exe"
    $bundleManifest = Join-Path $root "bundle-manifest.json"
    foreach ($path in @($exe, $browser, $backend, (Join-Path $root "config\policies.json"),
            (Join-Path $root "docs\SOURCE_OFFER.md"), (Join-Path $root "docs\PAYMENT_SECURITY.md"), $bundleManifest)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Portable package content missing: $path" }
    }
    $expectedBackendHash = "5b761c82022d77bd1229ebb9e5e7bc35353a7e3c6b842e33967a643d181c25b2"
    $actualBackendHash = (Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBackendHash -ne $expectedBackendHash) {
        throw "Packaged backend does not match the pinned trusted binary."
    }
    $backendVersion = (& $backend --version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not $backendVersion.Contains("privacy-browser-backend-v1.0.6") -or
        -not $backendVersion.Contains("7944a4c634834aac10a4e8e49934e326ac3f0e7a")) {
        throw "Packaged backend version provenance is incorrect."
    }
    $manifest = Get-Content -LiteralPath $bundleManifest -Raw | ConvertFrom-Json
    if ($manifest.releaseVersion -ne $version -or @($manifest.components).Count -lt 4) {
        throw "Bundle manifest release identity or component list is incomplete."
    }
    foreach ($component in $manifest.components) {
        $componentPath = Join-Path $root ([string]$component.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $componentPath -PathType Leaf)) {
            throw "Manifest component is missing: $($component.path)"
        }
        $item = Get-Item -LiteralPath $componentPath
        $actualHash = (Get-FileHash -LiteralPath $componentPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$component.length -or $actualHash -ne [string]$component.sha256) {
            throw "Manifest component does not match: $($component.path)"
        }
    }
    if (Get-ChildItem -LiteralPath $root -Recurse -File -Filter "MysteriumDark-Setup*.exe") {
        throw "Portable package must not contain or execute the upstream service-installing backend installer."
    }
    $info = (Get-Item -LiteralPath $exe).VersionInfo
    if ($info.FileVersion -ne "1.0.9.0" -or -not $info.ProductVersion.StartsWith("1.0.9")) {
        throw "Packaged executable version metadata is incorrect."
    }
    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Icon]::ExtractAssociatedIcon($exe)
    if (-not $icon) { throw "Packaged executable has no extractable application icon." }
    $embeddedBitmap = $icon.ToBitmap()
    $expectedBitmap = [Drawing.Bitmap]::FromFile((Join-Path (Split-Path $PSScriptRoot -Parent) "src\PrivacyBrowser.App\Assets\Icons\app-icon-32.png"))
    try {
        $comparison = New-Object Drawing.Bitmap 32, 32
        $graphics = [Drawing.Graphics]::FromImage($comparison)
        try { $graphics.DrawImage($embeddedBitmap, 0, 0, 32, 32) } finally { $graphics.Dispose() }
        $difference = 0L
        for ($y = 0; $y -lt 32; $y++) {
            for ($x = 0; $x -lt 32; $x++) {
                $a = $comparison.GetPixel($x, $y)
                $b = $expectedBitmap.GetPixel($x, $y)
                $difference += [Math]::Abs([int]$a.R - [int]$b.R)
                $difference += [Math]::Abs([int]$a.G - [int]$b.G)
                $difference += [Math]::Abs([int]$a.B - [int]$b.B)
            }
        }
        $meanDifference = $difference / (32 * 32 * 3)
        if ($meanDifference -gt 25) {
            throw "Packaged executable icon differs from the approved 1.0.9 artwork (mean channel difference $meanDifference)."
        }
    } finally {
        if ($comparison) { $comparison.Dispose() }
        $expectedBitmap.Dispose()
        $embeddedBitmap.Dispose()
        $icon.Dispose()
    }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host "PASS: portable package, dependencies, source offer, checksums, icon, and version metadata are valid."
