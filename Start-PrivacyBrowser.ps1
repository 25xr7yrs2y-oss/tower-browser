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

# Compatibility entry point for development scripts written before the product
# was normalized to Tower Browser. New automation should use Start-TowerBrowser.ps1.
& (Join-Path $PSScriptRoot "Start-TowerBrowser.ps1") `
    -NativeAppExe $NativeAppExe `
    -BrowserExe $BrowserExe `
    -BackendExe $BackendExe `
    -ProfilePath $ProfilePath `
    -InitialUrl $InitialUrl `
    -AdditionalBrowserArguments $AdditionalBrowserArguments `
    -KeepBackendRunning:$KeepBackendRunning `
    -SkipBackendLaunch:$SkipBackendLaunch
exit $LASTEXITCODE
