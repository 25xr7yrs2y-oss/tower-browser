$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = [xml](Get-Content (Join-Path $root "src\PrivacyBrowser.App\PrivacyBrowser.App.csproj") -Raw)
$properties = $project.Project.PropertyGroup

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "$Message (expected '$Expected', got '$Actual')" }
}

Assert-Equal $properties.Version "1.0.10" "Application version must be 1.0.10"
Assert-Equal $properties.VersionPrefix "1.0.10" "Application version prefix must be 1.0.10"
Assert-Equal $properties.PackageVersion "1.0.10" "Package version must be 1.0.10"
Assert-Equal $properties.AssemblyVersion "1.0.10.0" "Assembly version must be 1.0.10.0"
Assert-Equal $properties.FileVersion "1.0.10.0" "File version must be 1.0.10.0"
Assert-Equal $properties.InformationalVersion "1.0.10" "Informational version must be 1.0.10"
Assert-Equal $properties.ApplicationIcon "Assets\AppIcon.ico" "Executable icon declaration is missing"
Assert-Equal $properties.AssemblyName "TowerBrowser" "Executable name must use Tower Browser branding"
Assert-Equal $properties.Product "Tower Browser" "Product metadata must use Tower Browser branding"
Assert-Equal $properties.AssemblyTitle "Tower Browser" "Assembly title must use Tower Browser branding"

$assets = Join-Path $root "src\PrivacyBrowser.App\Assets"
$iconPath = Join-Path $assets "AppIcon.ico"
$masterPath = Join-Path $assets "IconMaster.png"
$sourcePath = Join-Path $assets "OfficialIconSource.png"
$windowXaml = Get-Content (Join-Path $root "src\PrivacyBrowser.App\MainWindow.xaml") -Raw
$manifest = Get-Content (Join-Path $root "src\PrivacyBrowser.App\app.manifest") -Raw
$packageScript = Get-Content (Join-Path $root "Package-Release.ps1") -Raw
$testWorkflow = Get-Content (Join-Path $root ".github\workflows\test.yml") -Raw
$releaseWorkflow = Get-Content (Join-Path $root ".github\workflows\release-package.yml") -Raw
if (-not $windowXaml.Contains('Icon="Assets/Icons/app-icon-256.png"')) { throw "The WPF window does not use the WPF-compatible official icon." }
if (-not $manifest.Contains('assemblyIdentity version="1.0.10.0"')) { throw "Manifest version is not 1.0.10.0." }
if (-not $manifest.Contains('name="TowerBrowser"')) { throw "Manifest application identity is inconsistent." }
foreach ($needle in @('$version = "1.0.10"', 'DEPENDENCIES_1.0.10.md', 'SOURCE_OFFER_1.0.10.md',
        'PAYMENT_SECURITY_1.0.10.md')) {
    if (-not $packageScript.Contains($needle)) { throw "Release package version invariant missing: $needle" }
}
foreach ($needle in @('releases/assets/537461102',
        '5b761c82022d77bd1229ebb9e5e7bc35353a7e3c6b842e33967a643d181c25b2',
        '7944a4c634834aac10a4e8e49934e326ac3f0e7a')) {
    if (-not $packageScript.Contains($needle)) { throw "Pinned backend provenance invariant missing: $needle" }
}
foreach ($needle in @('name: Tower Browser release', '- "v*.*.*"',
        "'^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'",
        'git merge-base --is-ancestor HEAD origin/main',
        'TowerBrowser-$VERSION-SHA256SUMS.txt', 'RELEASE_NOTES_$VERSION.md',
        'refusing to overwrite assets')) {
    if (-not $releaseWorkflow.Contains($needle)) { throw "General release workflow invariant missing: $needle" }
}
foreach ($needle in @('TowerBrowser-${{ steps.version.outputs.version }}-windows-x64-app',
        'TowerBrowser-${{ steps.version.outputs.version }}-windows-x64-self-contained',
        'branches:', '- main')) {
    if (-not $testWorkflow.Contains($needle)) { throw "Windows-test workflow invariant missing: $needle" }
}
foreach ($file in @('DEPENDENCIES_1.0.10.md', 'SOURCE_OFFER_1.0.10.md', 'RELEASE_NOTES_1.0.10.md',
        'PAYMENT_SECURITY_1.0.10.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "docs\$file") -PathType Leaf)) {
        throw "Release document is missing: $file"
    }
}

$resources = @($project.Project.ItemGroup.Resource | ForEach-Object { $_.Include })
if ('Assets\Icons\app-icon-256.png' -notin $resources) { throw "The WPF-compatible window icon is not embedded as a Resource." }

foreach ($path in @($iconPath, $masterPath, $sourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required icon asset missing: $path" }
}
$expectedIconHashes = @{
    "OfficialIconSource.png" = "d52bc0eb6908dea895db434b011cf8f851cd06aa52d790862e3ceb9c90cbad61"
    "IconMaster.png" = "d52bc0eb6908dea895db434b011cf8f851cd06aa52d790862e3ceb9c90cbad61"
    "AppIcon.ico" = "0d5e9949b67887406513e3026779797b17bd5d6476b31bd59a7b2d5075ae54d6"
    "Icons\app-icon-16.png" = "53d06593e4b2f9ed9ddc7270049e532d2c0913de7784769352f7ff4273e0502b"
    "Icons\app-icon-32.png" = "c14b6afe26a3f3385b2083afdf9179d8ce5acfdbfc138fffddb5b9394210d841"
    "Icons\app-icon-48.png" = "fdddb088a5f6a63222c3c4afc9db25a048e8091ee558e0863d21ddcb4a429374"
    "Icons\app-icon-256.png" = "a6b8359d11cd86ff0b1dff3e173706a7e83b5597d879301b282edb686473392c"
}
foreach ($relativePath in $expectedIconHashes.Keys) {
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $assets $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Equal $actualHash $expectedIconHashes[$relativePath] "Approved artwork hash is incorrect for $relativePath"
}
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256, 512)) {
    $png = Join-Path $assets "Icons\app-icon-$size.png"
    if (-not (Test-Path -LiteralPath $png -PathType Leaf)) { throw "PNG icon size missing: $size" }
}

# WPF uses the Windows Imaging Component decoder at runtime. Validate the
# exact window resource here; shell/PE icon extraction alone does not prove
# that a XAML ImageSource TypeConverter can decode it.
Add-Type -AssemblyName PresentationCore
$stream = [IO.File]::OpenRead($sourcePath)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    Assert-Equal $decoder.Frames.Count 1 "Sanitized source must contain one decodable PNG frame"
    Assert-Equal $decoder.Frames[0].PixelWidth 701 "Sanitized source width must preserve the complete supplied image"
    Assert-Equal $decoder.Frames[0].PixelHeight 701 "Sanitized source height must preserve the complete supplied image"
} finally {
    $stream.Dispose()
}

$windowIconPath = Join-Path $assets "Icons\app-icon-256.png"
$stream = [IO.File]::OpenRead($windowIconPath)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    Assert-Equal $decoder.Frames.Count 1 "WPF window icon must contain one decodable PNG frame"
    Assert-Equal $decoder.Frames[0].PixelWidth 256 "WPF window icon width is incorrect"
    Assert-Equal $decoder.Frames[0].PixelHeight 256 "WPF window icon height is incorrect"
} finally {
    $stream.Dispose()
}

$stream = [IO.File]::OpenRead($iconPath)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    Assert-Equal $decoder.Frames.Count 9 "Windows/WPF must decode all nine ICO frames"
    $wicIcoSizes = @($decoder.Frames | ForEach-Object { $_.PixelWidth })
    foreach ($expected in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        if ($expected -notin $wicIcoSizes) { throw "WIC-decoded ICO size entry missing: $expected" }
    }
} finally {
    $stream.Dispose()
}

$bytes = [IO.File]::ReadAllBytes($iconPath)
$count = [BitConverter]::ToUInt16($bytes, 4)
Assert-Equal $count 9 "ICO must contain nine size entries"
$icoSizes = @()
for ($i = 0; $i -lt $count; $i++) {
    $width = [int]$bytes[6 + ($i * 16)]
    if ($width -eq 0) { $width = 256 }
    $icoSizes += $width
}
foreach ($expected in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    if ($expected -notin $icoSizes) { throw "ICO size entry missing: $expected" }
}

$exe = Join-Path $root "app\TowerBrowser.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Built executable missing: $exe" }
$version = (Get-Item -LiteralPath $exe).VersionInfo
Assert-Equal $version.FileVersion "1.0.10.0" "Executable file version is incorrect"
if (-not $version.ProductVersion.StartsWith("1.0.10")) { throw "Executable product version is incorrect: $($version.ProductVersion)" }

Add-Type -AssemblyName System.Drawing
$embeddedIcon = [Drawing.Icon]::ExtractAssociatedIcon($exe)
if (-not $embeddedIcon) { throw "Executable does not contain an extractable application icon." }
$embeddedBitmap = $embeddedIcon.ToBitmap()
$expectedBitmap = [Drawing.Bitmap]::FromFile((Join-Path $assets "Icons\app-icon-32.png"))
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
    if ($meanDifference -gt 25) { throw "Embedded executable icon differs from the approved icon (mean channel difference $meanDifference)." }
} finally {
    if ($comparison) { $comparison.Dispose() }
    $expectedBitmap.Dispose()
    $embeddedBitmap.Dispose()
    $embeddedIcon.Dispose()
}

Write-Host "PASS: Tower Browser 1.0.10 metadata and the approved WPF-compatible/PE artwork are embedded."
