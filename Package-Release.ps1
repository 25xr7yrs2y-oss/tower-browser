[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "release"),
    [string]$MystReleaseToken = $env:MYST_RELEASE_TOKEN
)

$ErrorActionPreference = "Stop"
$version = "1.0.10"
$packageBase = "TowerBrowser-$version-windows-x64-portable"
$portableName = "$packageBase.zip"
$sourceName = "TowerBrowser-$version-myst-lmprove-source-7944a4c.tar.gz"
$checksumName = "TowerBrowser-$version-SHA256SUMS.txt"
$mullvadUrl = "https://github.com/mullvad/mullvad-browser/releases/download/15.0.14/mullvad-browser-windows-x86_64-15.0.14.exe"
$mullvadHash = "56d5e332b1e780c6413c1a88e7b0a855ec1df5a400a26d92f08585637bc75c02"
$mystAssetUrl = "https://api.github.com/repos/25xr7yrs2y-oss/myst-lmprove/releases/assets/537461102"
$mystHash = "5b761c82022d77bd1229ebb9e5e7bc35353a7e3c6b842e33967a643d181c25b2"
$mystCommit = "7944a4c634834aac10a4e8e49934e326ac3f0e7a"

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq "Core") {
    throw "Release packaging must run on Windows."
}
if ([string]::IsNullOrWhiteSpace($MystReleaseToken)) {
    throw "MYST_RELEASE_TOKEN is required to retrieve the pinned private backend binary and corresponding source."
}

$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
if (-not $sevenZip) { $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue }
if (-not $sevenZip) { throw "7-Zip is required for release packaging." }

function Invoke-SevenZipExtract {
    param([string]$Archive, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & $sevenZip.Source x $Archive "-o$Destination" -y | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed for $Archive with code $LASTEXITCODE." }
}

function Get-VerifiedDownload {
    param(
        [string]$Uri,
        [string]$Destination,
        [string]$ExpectedSha256,
        [hashtable]$Headers = @{}
    )
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -Headers $Headers -OutFile $Destination
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
        throw "Download did not produce $Destination."
    }
    if ($ExpectedSha256) {
        $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
            throw "SHA-256 mismatch for $Destination (expected $ExpectedSha256, got $actual)."
        }
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("tower-browser-release-" + [Guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $work $packageBase
$downloads = Join-Path $work "downloads"
$extract = Join-Path $work "extract"

try {
    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDirectory, $packageRoot, $downloads, $extract -Force | Out-Null

    $project = Join-Path $PSScriptRoot "src\PrivacyBrowser.App\PrivacyBrowser.App.csproj"
    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $packageRoot `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Self-contained application publish failed with code $LASTEXITCODE." }

    foreach ($file in @("README.md", "LICENSE", "THIRD_PARTY_NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageRoot
    }
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "config"), (Join-Path $packageRoot "docs") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "config\policies.json") -Destination (Join-Path $packageRoot "config\policies.json")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\DEPENDENCIES_1.0.10.md") -Destination (Join-Path $packageRoot "docs\DEPENDENCIES.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\SOURCE_OFFER_1.0.10.md") -Destination (Join-Path $packageRoot "docs\SOURCE_OFFER.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\PAYMENT_SECURITY_1.0.10.md") -Destination (Join-Path $packageRoot "docs\PAYMENT_SECURITY.md")

    $mullvadInstaller = Join-Path $downloads "mullvad-browser-15.0.14.exe"
    Get-VerifiedDownload -Uri $mullvadUrl -Destination $mullvadInstaller -ExpectedSha256 $mullvadHash
    $mullvadExtract = Join-Path $extract "mullvad"
    Invoke-SevenZipExtract -Archive $mullvadInstaller -Destination $mullvadExtract
    $browserExe = Get-ChildItem -LiteralPath $mullvadExtract -Recurse -File -Filter "mullvadbrowser.exe" | Select-Object -First 1
    if (-not $browserExe) { throw "The pinned Mullvad Browser installer did not contain mullvadbrowser.exe." }
    $browserDestination = Join-Path $packageRoot "vendor\mullvad-browser"
    New-Item -ItemType Directory -Path $browserDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $browserExe.Directory.FullName "*") -Destination $browserDestination -Recurse -Force

    $githubHeaders = @{
        Authorization = "Bearer $MystReleaseToken"
        Accept = "application/octet-stream"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $mystExe = Join-Path $downloads "myst-windows-x64.exe"
    Get-VerifiedDownload -Uri $mystAssetUrl -Destination $mystExe -ExpectedSha256 $mystHash -Headers $githubHeaders
    $mystDestination = Join-Path $packageRoot "vendor\myst-lmprove\resources\app.asar.unpacked\node_modules\@mysteriumnetwork\node\bin\win\x64"
    New-Item -ItemType Directory -Path $mystDestination -Force | Out-Null
    Copy-Item -LiteralPath $mystExe -Destination (Join-Path $mystDestination "myst.exe")

    # The archive checksum authenticates the download. This in-bundle manifest then
    # detects missing, stale, or mixed critical components after users extract it.
    $criticalComponents = @(
        (Join-Path $packageRoot "TowerBrowser.exe"),
        (Join-Path $packageRoot "config\policies.json"),
        (Join-Path $browserDestination "mullvadbrowser.exe"),
        (Join-Path $mystDestination "myst.exe")
    )
    $manifestComponents = foreach ($component in $criticalComponents) {
        $item = Get-Item -LiteralPath $component
        [PSCustomObject]@{
            path = [IO.Path]::GetRelativePath($packageRoot, $item.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length = $item.Length
        }
    }
    [PSCustomObject]@{
        releaseVersion = $version
        components = @($manifestComponents)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot "bundle-manifest.json") -Encoding UTF8

    $sourcePath = Join-Path $OutputDirectory $sourceName
    $sourceHeaders = @{
        Authorization = "Bearer $MystReleaseToken"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    Get-VerifiedDownload `
        -Uri "https://api.github.com/repos/25xr7yrs2y-oss/myst-lmprove/tarball/$mystCommit" `
        -Destination $sourcePath `
        -ExpectedSha256 "" `
        -Headers $sourceHeaders

    $portablePath = Join-Path $OutputDirectory $portableName
    Push-Location $work
    try {
        & $sevenZip.Source a -tzip -mx=7 $portablePath $packageBase | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Portable ZIP creation failed with code $LASTEXITCODE." }
    } finally {
        Pop-Location
    }

    $checksums = @()
    foreach ($path in @($portablePath, $sourcePath)) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums += "$hash  $([IO.Path]::GetFileName($path))"
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory $checksumName) -Value $checksums -Encoding Ascii

    Write-Host "Release package created: $portablePath"
    Write-Host "Corresponding source created: $sourcePath"
} finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
