$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Не найден компилятор C# из .NET Framework.' }
$source = Join-Path $repo 'src'
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$versionLine = Select-String -Path (Join-Path $source 'RazerBattery.cs') -Pattern 'AssemblyFileVersion\("([0-9.]+)"\)' | Select-Object -First 1
if (-not $versionLine) { throw 'Не удалось определить версию приложения.' }
$version = [regex]::Match($versionLine.Line, 'AssemblyFileVersion\("([0-9.]+)"\)').Groups[1].Value -replace '\.0$', ''
$exe = Join-Path $dist ("RazerBattery-{0}.exe" -f $version)
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    ('/win32manifest:' + (Join-Path $source 'RazerBattery.manifest')),
    ('/out:' + $exe), '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    (Join-Path $source 'NativeBattery.cs'),
    (Join-Path $source 'RazerBattery.cs'),
    (Join-Path $source 'BatteryHistory.cs'),
    (Join-Path $source 'WindowsAudioMute.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE" }
$archive = Join-Path $dist 'RazerBattery-Windows.zip'
Compress-Archive -LiteralPath $exe,(Join-Path $repo 'README.md'),(Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md') -DestinationPath $archive -Force
Write-Host "Готово: $exe"
Write-Host "Готово: $archive"
