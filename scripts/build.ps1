param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

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

$cmake = Resolve-CMake
$viewerProject = Join-Path $repoRoot "src/U3DViewer.Viewer/U3DViewer.Viewer.csproj"
$nativeSource = Join-Path $repoRoot "native/U3DViewer.NativeBridge"
$nativeBuild = Join-Path $repoRoot "build/native"

Push-Location $repoRoot
try {
    Write-Host "U3DViewer local build" -ForegroundColor Green
    Write-Host "Configuration: $Configuration"
    Write-Host "Target game:   selected later in the GUI"

    Invoke-External "Configure NativeBridge (x64)" {
        & $cmake -S $nativeSource -B $nativeBuild -A x64
    }

    Invoke-External "Build NativeBridge" {
        & $cmake --build $nativeBuild --config $Configuration --parallel
    }

    Invoke-External "Build U3DViewer.Viewer" {
        & dotnet build $viewerProject -c $Configuration --nologo
    }

    $nativeDll = Join-Path $nativeBuild "$Configuration/U3DViewer.NativeBridge.dll"
    $viewerExe = Join-Path $repoRoot "src/U3DViewer.Viewer/bin/$Configuration/net8.0/U3DViewer.Viewer.exe"
    $builderPayload = Join-Path $repoRoot "src/U3DViewer.Viewer/bin/$Configuration/net8.0/agent-builder"

    Write-Host ""
    Write-Host "Build completed." -ForegroundColor Green
    Write-Host "NativeBridge: $nativeDll"
    Write-Host "Viewer:       $viewerExe"
    Write-Host "Agent Builder:$builderPayload"
    Write-Host ""
    Write-Host "No gamePath/backend configuration is required. Start Viewer and choose a process or Open Game..." -ForegroundColor DarkCyan
}
finally {
    Pop-Location
}
