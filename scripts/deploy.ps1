param(
    [string]$ConfigPath = "u3dviewer.local.json",
    [switch]$LaunchViewer
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$configFile = if ([System.IO.Path]::IsPathRooted($ConfigPath)) {
    $ConfigPath
} else {
    Join-Path $repoRoot $ConfigPath
}

function Get-ConfigValue {
    param(
        [Parameter(Mandatory = $true)] $Config,
        [Parameter(Mandatory = $true)] [string] $Name,
        [string] $Default = ""
    )

    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    $value = [string]$property.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    return $value
}

if (-not (Test-Path $configFile)) {
    throw "Missing $ConfigPath. Copy u3dviewer.local.json.example to u3dviewer.local.json and edit backend/gamePath first."
}

$config = Get-Content -Raw $configFile | ConvertFrom-Json
$backend = (Get-ConfigValue -Config $config -Name "backend").Trim().ToUpperInvariant()
$configuration = Get-ConfigValue -Config $config -Name "configuration" -Default "Release"
$gamePath = Get-ConfigValue -Config $config -Name "gamePath"

if ($backend -notin @("MONO", "IL2CPP")) {
    throw "backend must be either 'Mono' or 'IL2CPP'."
}
if ($configuration -notin @("Debug", "Release")) {
    throw "configuration must be Debug or Release."
}
if ([string]::IsNullOrWhiteSpace($gamePath) -or -not (Test-Path $gamePath -PathType Container)) {
    throw "gamePath does not exist: '$gamePath'. Set it to the directory containing the target game executable."
}

$agentFolder = if ($backend -eq "MONO") { "U3DViewer.Agent.Mono" } else { "U3DViewer.Agent.IL2CPP" }
$agentTfm = if ($backend -eq "MONO") { "netstandard2.0" } else { "net6.0" }
$agentDll = Join-Path $repoRoot "src/$agentFolder/bin/$configuration/$agentTfm/$agentFolder.dll"
$protocolDll = Join-Path $repoRoot "src/U3DViewer.Protocol/bin/$configuration/netstandard2.0/U3DViewer.Protocol.dll"
$nativeDll = Join-Path $repoRoot "build/native/$configuration/U3DViewer.NativeBridge.dll"
$viewerDir = Join-Path $repoRoot "src/U3DViewer.Viewer/bin/$configuration/net8.0"
$viewerExe = Join-Path $viewerDir "U3DViewer.Viewer.exe"
$pluginDir = Join-Path $gamePath "BepInEx/plugins/U3DViewer"

foreach ($file in @($agentDll, $protocolDll, $nativeDll, $viewerExe)) {
    if (-not (Test-Path $file -PathType Leaf)) {
        throw "Build output is missing: $file. Run scripts/build.ps1 first."
    }
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

Copy-Item -Force $agentDll (Join-Path $pluginDir ([System.IO.Path]::GetFileName($agentDll)))
Copy-Item -Force $protocolDll (Join-Path $pluginDir "U3DViewer.Protocol.dll")
Copy-Item -Force $nativeDll (Join-Path $gamePath "U3DViewer.NativeBridge.dll")
Copy-Item -Force $nativeDll (Join-Path $viewerDir "U3DViewer.NativeBridge.dll")

Write-Host "U3DViewer deployed." -ForegroundColor Green
Write-Host "Backend:      $backend"
Write-Host "Game:         $gamePath"
Write-Host "Agent:        $pluginDir"
Write-Host "NativeBridge: $(Join-Path $gamePath 'U3DViewer.NativeBridge.dll')"
Write-Host "Viewer:       $viewerExe"

if ($LaunchViewer) {
    Write-Host "Launching Viewer..." -ForegroundColor Cyan
    Start-Process -FilePath $viewerExe -WorkingDirectory $viewerDir
}
