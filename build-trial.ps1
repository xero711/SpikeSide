$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/ValoAutoPick/ValoAutoPick.csproj') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
$distDir = 'dist'
$releaseExe = Join-Path $distDir "SpikeSide-Trial-$version-Win-x64.exe"
$publishDir = "artifacts/publish-trial-$version"
Push-Location -LiteralPath $projectRoot
try {
    dotnet run --project tests/ValoAutoPick.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet run --project tests/Trial/Trial.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Trial tests failed.' }
    dotnet run --project tests/UiMotion/UiMotion.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'UI animation checks failed.' }
    dotnet publish src/ValoAutoPick/ValoAutoPick.csproj -c Release -r win-x64 --self-contained true -p:TrialBuild=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Trial publish failed.' }
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir 'SpikeSide-Trial.exe') -Destination $releaseExe -Force
    Write-Host "Release asset: $releaseExe"
}
finally { Pop-Location }
