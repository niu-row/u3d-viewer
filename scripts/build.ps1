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

    if ($null -eq $Config) {
        return $Default
    }

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

function Resolve-CMake {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $editions = @("Community", "Professional", "Enterprise", "BuildTools")
    foreach ($edition in $editions) {
        $candidate = Join-Path $env:ProgramFiles "Microsoft Visual Studio/2022/$edition/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
        if (Test-Path $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "cmake.exe was not found. Install the Visual Studio 2022 'Desktop development with C++' workload with CMake tools."
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

function Try-SyncUnityReferences {
    param(
        [Parameter(Mandatory = $true)] [string] $Backend,
        [Parameter(Mandatory = $true)] [string] $GamePath,
        [Parameter(Mandatory = $true)] [string] $AgentLib,
        [Parameter(Mandatory = $true)] [string[]] $Files
    )

    if (Test-ReferenceSet -Directory $AgentLib -Files $Files) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($GamePath) -or -not (Test-Path $GamePath -PathType Container)) {
        return $false
    }

    $sourceDirectory = $null
    if ($Backend -eq "IL2CPP") {
        $candidate = Join-Path $GamePath "BepInEx/interop"
        if (Test-ReferenceSet -Directory $candidate -Files $Files) {
            $sourceDirectory = $candidate
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
    }

    if ($null -eq $sourceDirectory) {
        return $false
    }

    New-Item -ItemType Directory -Force -Path $AgentLib | Out-Null
    foreach ($file in $Files) {
        Copy-Item -Force (Join-Path $sourceDirectory $file) (Join-Path $AgentLib $file)
    }

    Write-Host "Staged $Backend Unity references from: $sourceDirectory" -ForegroundColor DarkCyan
    return $true
}

$config = $null
if (Test-Path $configFile -PathType Leaf) {
    try {
        $config = Get-Content -Raw $configFile | ConvertFrom-Json
    }
    catch {
        Write-Warning "Could not read $ConfigPath; continuing without local game configuration: $($_.Exception.Message)"
    }
}

$backend = (Get-ConfigValue -Config $config -Name "backend").Trim().ToUpperInvariant()
$gamePath = Get-ConfigValue -Config $config -Name "gamePath"

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = Get-ConfigValue -Config $config -Name "configuration" -Default "Release"
}
if ($Configuration -notin @("Debug", "Release")) {
    throw "configuration must be Debug or Release."
}

$cmake = Resolve-CMake
$viewerProject = Join-Path $repoRoot "src/U3DViewer.Viewer/U3DViewer.Viewer.csproj"
$nativeSource = Join-Path $repoRoot "native/U3DViewer.NativeBridge"
$nativeBuild = Join-Path $repoRoot "build/native"

$requiredUnityAssemblies = @(
    "UnityEngine.CoreModule.dll",
    "UnityEngine.SceneManagementModule.dll"
)

$agentDefinitions = @(
    [PSCustomObject]@{
        Backend = "MONO"
        Folder = "U3DViewer.Agent.Mono"
    },
    [PSCustomObject]@{
        Backend = "IL2CPP"
        Folder = "U3DViewer.Agent.IL2CPP"
    }
)

$agentsToBuild = @()
foreach ($definition in $agentDefinitions) {
    $agentLib = Join-Path $repoRoot "src/$($definition.Folder)/lib"
    $hasReferences = Test-ReferenceSet -Directory $agentLib -Files $requiredUnityAssemblies

    if (-not $hasReferences -and $backend -eq $definition.Backend) {
        $hasReferences = Try-SyncUnityReferences `
            -Backend $definition.Backend `
            -GamePath $gamePath `
            -AgentLib $agentLib `
            -Files $requiredUnityAssemblies
    }

    if ($hasReferences) {
        $agentsToBuild += $definition
    }
}

Push-Location $repoRoot
try {
    Write-Host "U3DViewer local build" -ForegroundColor Green
    Write-Host "Configuration: $Configuration"

    if (-not [string]::IsNullOrWhiteSpace($gamePath)) {
        Write-Host "Configured game: $gamePath"
    } else {
        Write-Host "Configured game: <none> (GUI game selection remains available)"
    }

    Invoke-External "Configure NativeBridge (x64)" {
        & $cmake -S $nativeSource -B $nativeBuild -A x64
    }

    Invoke-External "Build NativeBridge" {
        & $cmake --build $nativeBuild --config $Configuration --parallel
    }

    foreach ($definition in $agentsToBuild) {
        $agentProject = Join-Path $repoRoot "src/$($definition.Folder)/$($definition.Folder).csproj"
        Invoke-External "Build $($definition.Folder)" {
            & dotnet build $agentProject -c $Configuration --nologo
        }
    }

    if ($agentsToBuild.Count -eq 0) {
        Write-Warning "No Agent payload was built because no staged Unity references were found. Viewer/NativeBridge will still build. To enable GUI Install/Open Game for a backend, set gamePath once or stage that backend's Unity references."
    } elseif (-not [string]::IsNullOrWhiteSpace($backend) -and $backend -in @("MONO", "IL2CPP") -and -not ($agentsToBuild.Backend -contains $backend)) {
        Write-Warning "$backend Agent payload was not built because matching Unity references could not be found. Viewer/NativeBridge will still build."
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

    if ($agentsToBuild.Count -gt 0) {
        Write-Host "Agent payloads: $($agentsToBuild.Backend -join ', ')"
    }
}
finally {
    Pop-Location
}
