#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes TechConnect.Client and TechConnect.Server release binaries.

.DESCRIPTION
    Resolves the repository root from this script's location ($PSScriptRoot), never from
    the caller's working directory. Writes all artifacts under <repository-root>\release
    using the layout:

      release\<Project>\<rid>\

    Supported combinations:
      TechConnect.Client  win-x64   Self-contained managed (non-AOT) WinForms app
      TechConnect.Server  win-x64   Native AOT
      TechConnect.Server  linux-x64 Native AOT (cross-compiled from Windows via StuDev.AotAnywhere)

    Client is intentionally not Native AOT: WinForms + PublishAot is unsupported and
    previously produced binaries that failed to start. Do not reintroduce
    _SuppressWinFormsTrimError or Client PublishAot.

    Linux AOT from Windows uses the StuDev.AotAnywhere MSBuild SDK (Zig-based linker),
    referenced by TechConnect.Server and pinned in global.json. That SDK is restored by
    NuGet; this script does not replace it with WSL/Docker.

.PARAMETER Platform
    win-x64, linux-x64, or all.

.PARAMETER Project
    Optional filter: Client, Server, or All (default).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string] $Platform,

    [Parameter()]
    [ValidateSet('Client', 'Server', 'All')]
    [string] $Project = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ReleaseRoot = Join-Path $RepoRoot 'release'
$AotAnywhereSdkId = 'StuDev.AotAnywhere'
$AotAnywhereVersion = '1.1.0'

$Targets = @(
    @{
        Key                  = 'Client'
        Name                 = 'TechConnect.Client'
        Project              = Join-Path $RepoRoot 'src\TechConnect.Client\TechConnect.Client.csproj'
        ExeBase              = 'TechConnect.Client'
        Rids                 = @('win-x64')
        Framework            = 'net10.0-windows'
        PublishAot           = $false
        RequiresAotAnywhere  = $false
    },
    @{
        Key                  = 'Server'
        Name                 = 'TechConnect.Server'
        Project              = Join-Path $RepoRoot 'src\TechConnect.Server\TechConnect.Server.csproj'
        ExeBase              = 'TechConnect.Server'
        Rids                 = @('win-x64', 'linux-x64')
        Framework            = 'net10.0'
        PublishAot           = $true
        RequiresAotAnywhere  = $true
    }
)

function Fail {
    param([string] $Message)
    throw $Message
}

function Write-Step {
    param([string] $Message)
    Write-Host ""
    Write-Host "==> $Message"
}

function Get-ExeSuffix {
    param([string] $Rid)
    if ($Rid -like 'win-*') { return '.exe' }
    return ''
}

function Test-ReleaseRootSafety {
    param([string] $Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $expected = [System.IO.Path]::GetFullPath($ReleaseRoot)
    if (-not $full.StartsWith($expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "Refusing to modify path outside repository release root: $full"
    }
}

function Assert-DotNetSdk {
    Write-Step "Checking .NET SDK"
    $sdkLines = @(dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or $sdkLines.Count -eq 0) {
        Fail "dotnet SDK was not found. Install .NET SDK 10.0.404 (see global.json)."
    }

    $hasNet10 = $false
    foreach ($line in $sdkLines) {
        if ($line -match '^10\.') {
            $hasNet10 = $true
            break
        }
    }

    if (-not $hasNet10) {
        Fail "A .NET 10 SDK is required. Installed SDKs:`n$($sdkLines -join "`n")"
    }

    Write-Host "dotnet --version: $(dotnet --version)"
}

function Assert-AotAnywherePinned {
    $globalJsonPath = Join-Path $RepoRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath)) {
        Fail "global.json was not found at $globalJsonPath."
    }

    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    $pinned = $null
    if ($null -ne $globalJson.'msbuild-sdks' -and
        $null -ne $globalJson.'msbuild-sdks'.$AotAnywhereSdkId) {
        $pinned = [string]$globalJson.'msbuild-sdks'.$AotAnywhereSdkId
    }

    if ([string]::IsNullOrWhiteSpace($pinned)) {
        Fail "global.json must pin msbuild-sdks.$AotAnywhereSdkId for Linux AOT cross-compilation."
    }

    if ($pinned -ne $AotAnywhereVersion) {
        Write-Host "Warning: global.json pins $AotAnywhereSdkId $pinned (script expects $AotAnywhereVersion)."
    }

    Write-Host "Pinned $AotAnywhereSdkId $pinned"
}

function Get-SelectedRids {
    param([string] $PlatformName)

    if ($PlatformName -eq 'all') {
        return @('win-x64', 'linux-x64')
    }

    return @($PlatformName)
}

function Get-SelectedProjects {
    param([string] $ProjectFilter)

    switch ($ProjectFilter) {
        'Client' { return @($Targets | Where-Object { $_.Key -eq 'Client' }) }
        'Server' { return @($Targets | Where-Object { $_.Key -eq 'Server' }) }
        default { return @($Targets) }
    }
}

function Test-WindowsPeX64 {
    param([string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        Fail "Expected a Windows PE executable: $Path"
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or ($peOffset + 256) -ge $bytes.Length) {
        Fail "PE header is outside the file: $Path"
    }

    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
        Fail "Missing PE signature: $Path"
    }

    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) {
        Fail "Expected x64 (0x8664) and found machine 0x$('{0:X4}' -f $machine): $Path"
    }

    $optional = $peOffset + 24
    $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
    if ($magic -ne 0x20B) {
        Fail "Expected PE32+ and found magic 0x$('{0:X4}' -f $magic): $Path"
    }

    return @{
        Bytes    = $bytes
        Optional = $optional
    }
}

function Test-NativeAotBinary {
    param(
        [string] $Path,
        [string] $Rid
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Fail "Expected executable was not found: $Path"
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        Fail "Executable is empty: $Path"
    }

    if ($Rid -like 'win-*') {
        $pe = Test-WindowsPeX64 -Path $Path
        # CLR runtime header directory entry size (IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR).
        $clrSizeOffset = $pe.Optional + 112 + (14 * 8) + 4
        $clrSize = [System.BitConverter]::ToUInt32($pe.Bytes, $clrSizeOffset)
        if ($clrSize -ne 0) {
            Fail "Executable still has a CLR runtime header, so it is not Native AOT: $Path"
        }

        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        Fail "Expected a Linux ELF executable: $Path"
    }

    if ($bytes[4] -ne 2) {
        Fail "Expected a 64-bit ELF executable: $Path"
    }

    if ($bytes[5] -ne 1) {
        Fail "Expected a little-endian ELF executable: $Path"
    }

    $elfType = [System.BitConverter]::ToUInt16($bytes, 16)
    if ($elfType -ne 2 -and $elfType -ne 3) {
        Fail "Expected an ELF executable (ET_EXEC or ET_DYN) and found type ${elfType}: $Path"
    }

    $elfMachine = [System.BitConverter]::ToUInt16($bytes, 18)
    if ($elfMachine -ne 62) {
        Fail "Expected ELF x86-64 (62) and found machine ${elfMachine}: $Path"
    }
}

function Test-SelfContainedManagedBinary {
    param(
        [string] $Directory,
        [string] $ExeBase,
        [string] $ExePath
    )

    if (-not (Test-Path -LiteralPath $ExePath)) {
        Fail "Expected executable was not found: $ExePath"
    }

    $item = Get-Item -LiteralPath $ExePath
    if ($item.Length -le 0) {
        Fail "Executable is empty: $ExePath"
    }

    $null = Test-WindowsPeX64 -Path $ExePath

    $managedDll = Join-Path $Directory ($ExeBase + '.dll')
    if (-not (Test-Path -LiteralPath $managedDll)) {
        Fail "Self-contained Client publish is missing managed assembly: $managedDll"
    }

    $requiredRuntime = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')
    foreach ($name in $requiredRuntime) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path)) {
            Fail "Self-contained Client publish is missing bundled runtime file '$name' under $Directory."
        }
    }

    # Framework-dependent deployments place runtimeconfig.json that depends on a shared framework.
    # Self-contained publishes still emit *.runtimeconfig.json; ensure deps.json exists with the app.
    $deps = Join-Path $Directory ($ExeBase + '.deps.json')
    if (-not (Test-Path -LiteralPath $deps)) {
        Fail "Self-contained Client publish is missing dependency manifest: $deps"
    }
}

function Assert-NoManagedRuntimePayload {
    param([string] $Directory)

    $forbidden = @(
        'hostfxr.dll', 'coreclr.dll', 'hostpolicy.dll',
        'libhostfxr.so', 'libcoreclr.so', 'libhostpolicy.so',
        'hostfxr.dylib', 'libcoreclr.dylib'
    )

    foreach ($name in $forbidden) {
        $path = Join-Path $Directory $name
        if (Test-Path -LiteralPath $path) {
            Fail "Shared runtime file '$name' is present under $Directory. Native AOT output must not include it."
        }
    }
}

function Remove-NonReleaseConfigFiles {
    param([string] $Directory)

    $removeNames = @(
        'appsettings.Development.json',
        'appsettings.Staging.json',
        'dev-certs.json',
        'dev-certs.local.json'
    )

    foreach ($name in $removeNames) {
        $path = Join-Path $Directory $name
        if (Test-Path -LiteralPath $path) {
            Write-Host "Removing non-release file: $name"
            Remove-Item -LiteralPath $path -Force
        }
    }
}

function Publish-OneTarget {
    param(
        [hashtable] $App,
        [string] $Rid
    )

    if ($App.Rids -notcontains $Rid) {
        Fail ("Unsupported target {0} / {1}. Supported RIDs for {0}: {2}" -f $App.Name, $Rid, ($App.Rids -join ', '))
    }

    if (-not (Test-Path -LiteralPath $App.Project)) {
        Fail "Project file was not found: $($App.Project)"
    }

    $outDir = Join-Path (Join-Path $ReleaseRoot $App.Name) $Rid
    Test-ReleaseRootSafety -Path $outDir

    $modeLabel = if ($App.PublishAot) { 'Native AOT' } else { 'self-contained (non-AOT)' }
    Write-Step "Publishing $($App.Name) ($Rid) [$modeLabel] -> $outDir"

    if (Test-Path -LiteralPath $outDir) {
        Remove-Item -LiteralPath $outDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $exeSuffix = Get-ExeSuffix -Rid $Rid
    $publishArgs = @(
        'publish', $App.Project,
        '-c', 'Release',
        '-f', $App.Framework,
        '-r', $Rid,
        '--self-contained', 'true',
        '-p:SelfContained=true',
        '-o', $outDir,
        '--nologo'
    )

    if ($App.PublishAot) {
        # IlcTreatWarningsAsErrors=false: Serilog.Settings.Configuration and Microsoft.DevTunnels.Ssh
        # still emit IL2104/IL300x/IL3053 analysis warnings under Native AOT. C# TreatWarningsAsErrors
        # remains in force for project code; this only avoids failing the ILC step on third-party packages.
        $publishArgs += @(
            '-p:PublishAot=true',
            '-p:IlcTreatWarningsAsErrors=false'
        )
    }
    else {
        # Explicitly disable AOT/trimming for the WinForms Client. Do not suppress NETSDK1175.
        $publishArgs += @(
            '-p:PublishAot=false',
            '-p:PublishTrimmed=false'
        )
    }

    Write-Host ("dotnet " + ($publishArgs -join ' '))
    & dotnet @publishArgs
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        Fail "dotnet publish failed for $($App.Name) ($Rid) with exit code $publishExit."
    }

    Remove-NonReleaseConfigFiles -Directory $outDir

    $exePath = Join-Path $outDir ($App.ExeBase + $exeSuffix)
    if (-not (Test-Path -LiteralPath $exePath)) {
        Fail "Publish did not produce expected executable: $exePath"
    }

    if ($App.PublishAot) {
        $managedDll = Join-Path $outDir ($App.ExeBase + '.dll')
        if (Test-Path -LiteralPath $managedDll) {
            Fail "Managed assembly '$($App.ExeBase).dll' is present. Native AOT output must not include it."
        }

        Assert-NoManagedRuntimePayload -Directory $outDir
        Test-NativeAotBinary -Path $exePath -Rid $Rid
    }
    else {
        Test-SelfContainedManagedBinary -Directory $outDir -ExeBase $App.ExeBase -ExePath $exePath
    }

    if (-not (Test-Path -LiteralPath (Join-Path $outDir 'appsettings.json'))) {
        Fail "Missing required configuration file: $(Join-Path $outDir 'appsettings.json')"
    }

    $size = (Get-Item -LiteralPath $exePath).Length
    Write-Host ("OK  {0}  ({1:N0} bytes)  {2} verified" -f $exePath, $size, $modeLabel)
}

function Show-ReleaseSummary {
    Write-Step "Release summary ($ReleaseRoot)"
    if (-not (Test-Path -LiteralPath $ReleaseRoot)) {
        Write-Host "(empty)"
        return
    }

    Get-ChildItem -LiteralPath $ReleaseRoot -Recurse -File -Force |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($ReleaseRoot.Length).TrimStart('\', '/')
            Write-Host ("{0,12:N0}  {1}" -f $_.Length, $relative)
        }
}

try {
    Write-Host "Repository root: $RepoRoot"
    Write-Host "Release root:    $ReleaseRoot"

    if (-not $ReleaseRoot.Equals((Join-Path $RepoRoot 'release'), [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "Internal error: release root is not <repo>\release."
    }

    # Honour repository global.json regardless of the caller's working directory.
    Set-Location -LiteralPath $RepoRoot

    Assert-DotNetSdk
    Assert-AotAnywherePinned

    $rids = @(Get-SelectedRids -PlatformName $Platform)
    $apps = @(Get-SelectedProjects -ProjectFilter $Project)
    $planned = New-Object System.Collections.Generic.List[object]

    foreach ($app in $apps) {
        foreach ($rid in $rids) {
            if ($app.Rids -contains $rid) {
                $planned.Add([pscustomobject]@{ App = $app; Rid = $rid }) | Out-Null
            }
            elseif ($rids.Count -eq 1 -and $apps.Count -eq 1) {
                # Explicit single project + single RID request for an unsupported pair.
                Fail ("Unsupported target {0} / {1}. Supported RIDs for {0}: {2}" -f $app.Name, $rid, ($app.Rids -join ', '))
            }
            elseif ($Project -eq 'Client' -and $rid -like 'linux-*') {
                Fail "TechConnect.Client is a Windows Forms (net10.0-windows) application and does not support linux-x64 publishing."
            }
        }
    }

    if ($planned.Count -eq 0) {
        Fail "No publish targets matched Platform='$Platform' Project='$Project'."
    }

    Write-Step "Planned targets"
    foreach ($pair in $planned) {
        $mode = if ($pair.App.PublishAot) { 'Native AOT' } else { 'self-contained non-AOT' }
        $cross = ''
        if ($pair.Rid -like 'linux-*' -and $pair.App.RequiresAotAnywhere) {
            $cross = " via StuDev.AotAnywhere $AotAnywhereVersion"
        }
        Write-Host ("- {0} / {1} ({2}{3})" -f $pair.App.Name, $pair.Rid, $mode, $cross)
    }

    foreach ($pair in $planned) {
        Publish-OneTarget -App $pair.App -Rid $pair.Rid
    }

    Show-ReleaseSummary
    Write-Host ""
    Write-Host "Publish-Release completed successfully."
    exit 0
}
catch {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}
