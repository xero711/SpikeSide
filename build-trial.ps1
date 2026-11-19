$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/ValoAutoPick/ValoAutoPick.csproj') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
$distDir = "dist/SpikeSide-Trial-$version-Win-x64"
$publishDir = "artifacts/publish-trial-$version"
$zipPath = "dist/SpikeSide-Trial-$version-Win-x64.zip"
Push-Location -LiteralPath $projectRoot
try {
    dotnet run --project tests/ValoAutoPick.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet run --project tests/Trial/Trial.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Trial tests failed.' }
    dotnet publish src/ValoAutoPick/ValoAutoPick.csproj -c Release -r win-x64 --self-contained true -p:TrialBuild=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Trial publish failed.' }
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir 'SpikeSide-Trial.exe') -Destination $distDir -Force
    $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages'
    $assets = Get-Content -LiteralPath 'src/ValoAutoPick/obj/project.assets.json' -Raw | ConvertFrom-Json
    $runtimePackage = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object { $_.name -ieq 'Microsoft.NETCore.App.Runtime.win-x64' } | Select-Object -First 1
    if (-not $runtimePackage) { throw 'Runtime licenses were not restored.' }
    $runtimeVersion = $runtimePackage.version.Trim('[', ']').Split(',')[0].Trim()
    $licenses = @{
        'microsoft.web.webview2\1.0.4314.35\LICENSE.txt' = 'WebView2-LICENSE.txt'
        'system.security.cryptography.protecteddata\8.0.0\LICENSE.TXT' = 'ProtectedData-LICENSE.txt'
        'system.security.cryptography.protecteddata\8.0.0\THIRD-PARTY-NOTICES.TXT' = 'ProtectedData-THIRD-PARTY-NOTICES.txt'
        "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT" = 'DOTNET-LICENSE.txt'
        "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT" = 'DOTNET-THIRD-PARTY-NOTICES.txt'
    }
    foreach ($entry in $licenses.GetEnumerator()) {
        Copy-Item -LiteralPath (Join-Path $packageRoot $entry.Key) -Destination (Join-Path $distDir $entry.Value) -Force
    }
    $files = @('SpikeSide-Trial.exe') + @($licenses.Values)
    Compress-Archive -LiteralPath @($files | ForEach-Object { Join-Path $distDir $_ }) -DestinationPath $zipPath -Force
    Write-Host "Ready: $distDir/SpikeSide-Trial.exe"
}
finally { Pop-Location }
