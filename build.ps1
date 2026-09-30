$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet run --project tests/PocketReader.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw '检查失败，停止发布。' }
$releasePath = Join-Path $PSScriptRoot 'app'
$running = Get-Process Read_me -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $releasePath 'Read_me.exe') }
if ($running) { throw '请关闭 app 中的 Read_me 后再更新。' }
$libraryPath = Join-Path $releasePath 'ReaderData'
if (Test-Path -LiteralPath $libraryPath) {
    $backupDirectory = Join-Path $PSScriptRoot 'backups'
    New-Item -Path $backupDirectory -ItemType Directory -Force | Out-Null
    $backupPath = Join-Path $backupDirectory ('ReaderData-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.zip')
    Compress-Archive -LiteralPath $libraryPath -DestinationPath $backupPath
}
dotnet publish src/PocketReader.csproj -c Release --no-self-contained -o $releasePath
if ($LASTEXITCODE -ne 0) { throw '发布失败。书库和备份已保留。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $releasePath '使用说明.md') -Force
Write-Output ('已更新：' + (Join-Path $releasePath 'Read_me.exe'))
