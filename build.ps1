# Builds OpenM50Webcam.exe (C#) and OpenM50VCam.dll (C++) into .\dist
# Requires: .NET 8 SDK, Visual Studio 2022 (Build Tools) with C++ workload and Windows 11 SDK.
# 'Continue': native tools write harmless messages to stderr; failures are checked via exit codes.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# --- C# app
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
& $dotnet publish (Join-Path $root 'src\OpenM50Webcam') -c Release -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- C++ virtual camera DLL
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ build tools not found' }
& (Join-Path $root 'src\OpenM50VCam\build.cmd') "`"$vs\VC\Auxiliary\Build\vcvars64.bat`""
if ($LASTEXITCODE -ne 0) { throw 'C++ build failed' }

# --- Installer (optional, needs Inno Setup 6)
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    [xml]$proj = Get-Content (Join-Path $root 'src\OpenM50Webcam\OpenM50Webcam.csproj')
    $version = $proj.Project.PropertyGroup.Version | Select-Object -First 1
    & $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\OpenM50Webcam.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
} else {
    Write-Host 'Inno Setup 6 not found - skipping installer.' -ForegroundColor Yellow
}
Write-Host "Build OK -> $dist"
