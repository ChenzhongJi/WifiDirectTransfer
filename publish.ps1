[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.5.1',
    [string]$Label,
    [switch]$IncludePdb,
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

Push-Location $repoRoot

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK 8 is required but was not found on PATH.'
    }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'git is required to determine the build label.'
    }

    $commit = (& git rev-parse --short HEAD).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the current git commit.'
    }

    $fullCommit = (& git rev-parse HEAD).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the current git commit.'
    }

    $status = @(& git status --porcelain)

    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the git working tree status.'
    }

    $isDirty = $status.Count -gt 0

    if ($isDirty -and -not $AllowDirty) {
        throw 'The working tree has uncommitted changes. Commit them or pass -AllowDirty.'
    }

    if ([string]::IsNullOrWhiteSpace($Label)) {
        $Label = "$Version-preview-$commit"

        if ($isDirty) {
            $Label += '-dirty'
        }
    }

    foreach ($invalidChar in [IO.Path]::GetInvalidFileNameChars()) {
        if ($Label.IndexOf($invalidChar) -ge 0) {
            throw "Label contains an invalid file name character: $Label"
        }
    }

    $running = Get-Process -Name 'WifiDirectTransfer.App' -ErrorAction SilentlyContinue

    if ($running) {
        throw 'WifiDirectTransfer.App is running. Close it before packaging.'
    }

    $solution = Join-Path $repoRoot 'WifiDirectTransfer.sln'
    $project = Join-Path $repoRoot 'src\WifiDirectTransfer.App\WifiDirectTransfer.App.csproj'
    $readme = Join-Path $repoRoot 'README.md'
    $changelog = Join-Path $repoRoot 'CHANGELOG.md'
    $outputRoot = Join-Path $repoRoot 'publish'
    $publishDir = Join-Path $outputRoot "WifiDirectTransfer-$Runtime"
    $artifactsDir = Join-Path $outputRoot 'artifacts'
    $stageDir = Join-Path $artifactsDir "stage-$Label"
    $zipPath = Join-Path $artifactsDir "WifiDirectTransfer-$Label-$Runtime.zip"
    $hashPath = "$zipPath.sha256"

    foreach ($requiredFile in @($solution, $project, $readme, $changelog)) {
        if (-not (Test-Path -LiteralPath $requiredFile)) {
            throw "Required file not found: $requiredFile"
        }
    }

    Write-Host '==> Clean previous output'
    foreach ($path in @($publishDir, $stageDir, $zipPath, $hashPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null

    Write-Host '==> Restore'
    & dotnet restore $solution --nologo

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore failed.'
    }

    Write-Host '==> Build'
    & dotnet build $solution -c $Configuration --no-restore --nologo

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }

    if (-not $SkipTests) {
        Write-Host '==> Test'
        & dotnet test $solution -c $Configuration --no-build --nologo

        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet test failed.'
        }
    }

    Write-Host '==> Publish'
    & dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    $publishedExe = Join-Path $publishDir 'WifiDirectTransfer.App.exe'

    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Published executable not found: $publishedExe"
    }

    Write-Host '==> Stage package contents'
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    Get-ChildItem -LiteralPath $publishDir -File |
        Where-Object { $IncludePdb -or $_.Extension -ne '.pdb' } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $stageDir -Force
        }

    Copy-Item -LiteralPath $readme -Destination $stageDir -Force
    Copy-Item -LiteralPath $changelog -Destination $stageDir -Force

    $stagedExe = Join-Path $stageDir 'WifiDirectTransfer.App.exe'

    if (-not (Test-Path -LiteralPath $stagedExe)) {
        throw 'Staged executable is missing.'
    }

    $dllCount = @(Get-ChildItem -LiteralPath $stageDir -Filter '*.dll' -File).Count
    Write-Host "    Native DLLs: $dllCount"
    Write-Host "    README: $(Test-Path -LiteralPath (Join-Path $stageDir 'README.md'))"
    Write-Host "    CHANGELOG: $(Test-Path -LiteralPath (Join-Path $stageDir 'CHANGELOG.md'))"

    Write-Host '==> Create zip'
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    $hashLine = "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zipPath))"
    Set-Content -LiteralPath $hashPath -Value $hashLine -Encoding ASCII

    $zipSizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)

    Write-Host ''
    Write-Host 'Package complete.'
    Write-Host "  Version:  $Version+$commit"
    Write-Host "  Commit:   $fullCommit"
    Write-Host "  Zip:      $zipPath"
    Write-Host "  Size:     $zipSizeMb MB"
    Write-Host "  SHA256:   $($hash.Hash.ToLowerInvariant())"
    Write-Host "  Hash:     $hashPath"

    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
finally {
    Pop-Location
}
