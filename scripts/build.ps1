param(
    [string]$ConfigPath = "u3dviewer.local.json",
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

function Test-ReferenceSet {
    param(
        [Parameter(Mandatory = $true)] [string] $Directory,
        [Parameter(Mandatory = $true)] [string[]] $Files
    )

    if (-not (Test-Path $Directory -PathType Container)) {
        return $false
    }

    foreach ($file in $Files) {
        if (-not (Test-Path (Join-Path $Directory $file) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

function Sync-UnityReferences {
    param(
        [Parameter(Mandatory = $true)] [string] $Backend,
        [Parameter(Mandatory = $true)] [string] $GamePath,
        [Parameter(Mandatory = $true)] [string] $AgentLib,
        [Parameter(Mandatory = $true)] [string[]] $Files
    )

    if (Test-ReferenceSet -Directory $AgentLib -Files $Files) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($GamePath) -or -not (Test-Path $GamePath -PathType Container)) {
        throw "Unity references are missing and gamePath is not valid. Set gamePath in u3dviewer.local.json."
    }

    $sourceDirectory = $null
    if ($Backend -eq "IL2CPP") {
        $candidate = Join-Path $GamePath "BepInEx/interop"
        if (Test-ReferenceSet -Directory $candidate -Files $Files) {
            $sourceDirectory = $candidate
        } else {
            throw "IL2CPP interop references were not found in '$candidate'. Run the game with BepInEx once, then build again."
        }
    } else {
        $dataDirectories = Get-ChildItem -Path $GamePath -Directory -Filter "*_Data" -ErrorAction SilentlyContinue
        foreach ($dataDirectory in $dataDirectories) {
            $candidate = Join-Path $dataDirectory.FullName "Managed"
            if (Test-ReferenceSet -Directory $candidate -Files $Files) {
                $sourceDirectory = $candidate
                break
            }
        }

        if ($null -eq $sourceDirectory) {
            throw "Mono Unity references were not found under '$GamePath/*_Data/Managed'."
        }
    }

    New-Item -ItemType Directory -Force -Path $AgentLib | Out-Null
    foreach ($file in $Files) {
        Copy-Item -Force (Join-Path $sourceDirectory $file) (Join-Path $AgentLib $file)
    }

    Write-Host "Staged Unity references from: $sourceDirectory" -ForegroundColor DarkCyan
}

if (-not (Test-Path $configFile)) {
    throw "Missing $ConfigPath. Run the VSCode task 'U3DViewer: Create Local Config', then edit backend/gamePath."
}

$config = Get-Content -Raw $configFile | ConvertFrom-Json
$backend = (Get-ConfigValue -Config $config -Name "backend").Trim().ToUpperInvariant()
$gamePath = Get-ConfigValue -Config $config -Name "gamePath"
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

Sync-UnityReferences -Backend $backend -GamePath $gamePath -AgentLib $agentLib -Files $requiredUnityAssemblies

Push-Location $repoRoot
try {
    Write-Host "U3DViewer local build" -ForegroundColor Green
    Write-Host "Backend:       $backend"
    Write-Host "Configuration: $Configuration"
    Write-Host "Game:          $gamePath"

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
}
finally {
    Pop-Location
}
