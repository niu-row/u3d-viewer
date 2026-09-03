param(
    [string]$ConfigPath = "u3dviewer.local.json",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = ""
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

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [scriptblock] $Command
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $configFile)) {
    throw "Missing $ConfigPath. Copy u3dviewer.local.json.example to u3dviewer.local.json and edit backend/gamePath first."
}

$config = Get-Content -Raw $configFile | ConvertFrom-Json
$backend = (Get-ConfigValue -Config $config -Name "backend").Trim().ToUpperInvariant()
if ($backend -notin @("MONO", "IL2CPP")) {
    throw "backend must be either 'Mono' or 'IL2CPP' in $ConfigPath."
}

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = Get-ConfigValue -Config $config -Name "configuration" -Default "Release"
}
if ($Configuration -notin @("Debug", "Release")) {
    throw "configuration must be Debug or Release."
}

$agentFolder = if ($backend -eq "MONO") { "U3DViewer.Agent.Mono" } else { "U3DViewer.Agent.IL2CPP" }
$agentProject = Join-Path $repoRoot "src/$agentFolder/$agentFolder.csproj"
$agentLib = Join-Path $repoRoot "src/$agentFolder/lib"
$viewerProject = Join-Path $repoRoot "src/U3DViewer.Viewer/U3DViewer.Viewer.csproj"
$nativeSource = Join-Path $repoRoot "native/U3DViewer.NativeBridge"
$nativeBuild = Join-Path $repoRoot "build/native"

$requiredUnityAssemblies = @(
    "UnityEngine.CoreModule.dll",
    "UnityEngine.SceneManagementModule.dll"
)

$missingUnityAssemblies = @()
foreach ($assembly in $requiredUnityAssemblies) {
    if (-not (Test-Path (Join-Path $agentLib $assembly))) {
        $missingUnityAssemblies += $assembly
    }
}

if ($missingUnityAssemblies.Count -gt 0) {
    $sourceHint = if ($backend -eq "MONO") {
        "Copy them from <Game>_Data/Managed/."
    } else {
        "Run the IL2CPP game with BepInEx once, then copy them from <Game>/BepInEx/interop/."
    }

    throw "Missing Unity references in src/$agentFolder/lib/: $($missingUnityAssemblies -join ', '). $sourceHint"
}

Push-Location $repoRoot
try {
    Write-Host "U3DViewer local build" -ForegroundColor Green
    Write-Host "Backend:       $backend"
    Write-Host "Configuration: $Configuration"

    Invoke-External "Configure NativeBridge (x64)" {
        & cmake -S $nativeSource -B $nativeBuild -A x64
    }

    Invoke-External "Build NativeBridge" {
        & cmake --build $nativeBuild --config $Configuration --parallel
    }

    Invoke-External "Build $agentFolder" {
        & dotnet build $agentProject -c $Configuration --nologo
    }

    Invoke-External "Build U3DViewer.Viewer" {
        & dotnet build $viewerProject -c $Configuration --nologo
    }

    $nativeDll = Join-Path $nativeBuild "$Configuration/U3DViewer.NativeBridge.dll"
    $viewerExe = Join-Path $repoRoot "src/U3DViewer.Viewer/bin/$Configuration/net8.0/U3DViewer.Viewer.exe"

    Write-Host ""
    Write-Host "Build completed." -ForegroundColor Green
    Write-Host "NativeBridge: $nativeDll"
    Write-Host "Viewer:       $viewerExe"
    Write-Host "Run scripts/deploy.ps1 to copy the game-side files."
}
finally {
    Pop-Location
}
