[CmdletBinding()]
param(
    [string]$NativeAppExe = (Join-Path $PSScriptRoot "app\TowerBrowser.exe"),
    [string]$BrowserExe = (Join-Path $PSScriptRoot "vendor\mullvad-browser\mullvadbrowser.exe"),
    [string]$BackendExe = (Join-Path $PSScriptRoot "vendor\myst-lmprove\resources\app.asar.unpacked\node_modules\@mysteriumnetwork\node\bin\win\x64\myst.exe"),
    [string]$ProfilePath = (Join-Path $PSScriptRoot "state\profile"),
    [string]$InitialUrl = "about:blank",
    [string[]]$AdditionalBrowserArguments = @(),
    [switch]$KeepBackendRunning,
    [switch]$SkipBackendLaunch
)

$ErrorActionPreference = "Stop"

foreach ($requiredFile in @($NativeAppExe, $BrowserExe, (Join-Path $PSScriptRoot "config\policies.json"))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file not found: $requiredFile"
    }
}
if (-not $SkipBackendLaunch -and -not (Test-Path -LiteralPath $BackendExe -PathType Leaf)) {
    throw "Backend executable not found: $BackendExe"
}

$arguments = @(
    "--bundle-root", $PSScriptRoot,
    "--browser-exe", (Resolve-Path $BrowserExe).Path,
    "--backend-exe", $BackendExe,
    "--profile", $ProfilePath,
    "--initial-url", $InitialUrl
)
foreach ($argument in $AdditionalBrowserArguments) {
    $arguments += @("--browser-arg", $argument)
}
if ($KeepBackendRunning) { $arguments += "--keep-backend-running" }
if ($SkipBackendLaunch) { $arguments += "--skip-backend-launch" }

# The native application owns UI, backend lifecycle, and browser startup.
# No browser is opened for controls and no HTTP UI server is started.
& $NativeAppExe @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Tower Browser exited with code $LASTEXITCODE."
}
