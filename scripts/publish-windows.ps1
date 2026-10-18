param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\win-x64"
$archivePath = Join-Path $repositoryRoot "artifacts\OnlyShell-win-x64.zip"
$projectPath = Join-Path $repositoryRoot "src\OnlyShell.App\OnlyShell.App.csproj"

New-Item -ItemType Directory -Path (Split-Path -Parent $publishDirectory) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $archivePath) -Force | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:Version=$Version `
    -p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Published OnlyShell $Version"
Write-Host "Folder: $publishDirectory"
Write-Host "Archive: $archivePath"
