param([string]$Dotnet = 'dotnet', [string]$MakeNsis = 'makensis')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'src/BudsTray/RedmiBudsTray.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version
$artifactDir = Join-Path $repoRoot 'artifacts'
$payloadDir = Join-Path $artifactDir 'portable'
New-Item -ItemType Directory -Force $payloadDir | Out-Null
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payloadDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
Get-ChildItem $payloadDir -Filter '*.pdb' | Remove-Item
Copy-Item (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD_PARTY.md'), (Join-Path $repoRoot 'TOOTHTRAY-LICENSE.md') $payloadDir -Force
$installerPath = Join-Path $artifactDir "BudsTray-Setup-$version.exe"
& $MakeNsis /INPUTCHARSET UTF8 "/DVERSION=$version" "/DPAYLOAD=$payloadDir" "/DOUTFILE=$installerPath" (Join-Path $repoRoot 'installer/BudsTray.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Compress-Archive -Path "$payloadDir/*" -DestinationPath (Join-Path $artifactDir 'BudsTray-win-x64.zip') -Force
Get-FileHash $installerPath, (Join-Path $artifactDir 'BudsTray-win-x64.zip') -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $artifactDir 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Built $installerPath"
