param(
	[string]$Configuration = 'Debug'
)

$root = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $root 'PublishShim.MSBuild.Tasks/PublishShim.MSBuild.Tasks.csproj'
$consoleShimProject = Join-Path $root 'PublishShim.Exe/PublishShim.Exe.vcxproj'
$guiShimProject = Join-Path $root 'PublishShim.WinExe/PublishShim.WinExe.vcxproj'
$tasksDirectory = Join-Path $root 'tasks'
$toolsDirectory = Join-Path $root 'tools/win-x64'

 dotnet build $taskProject -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

msbuild $consoleShimProject /p:Configuration=$Configuration /p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

msbuild $guiShimProject /p:Configuration=$Configuration /p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$taskOutput = Join-Path $root "PublishShim.MSBuild.Tasks/bin/$Configuration/net8.0"
$consoleShimOutput = Get-ChildItem -Path $root -Recurse -Filter 'publishshim.exe' | Where-Object { $_.FullName -like "*$Configuration*" } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$guiShimOutput = Get-ChildItem -Path $root -Recurse -Filter 'publishshim.winexe.exe' | Where-Object { $_.FullName -like "*$Configuration*" } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $consoleShimOutput) { throw 'Console native shim output was not found.' }
if (-not $guiShimOutput) { throw 'GUI native shim output was not found.' }

New-Item -ItemType Directory -Force -Path $tasksDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
Copy-Item -Path (Join-Path $taskOutput '*') -Destination $tasksDirectory -Recurse -Force
Copy-Item -Path $consoleShimOutput.FullName -Destination (Join-Path $toolsDirectory 'publishshim.exe') -Force
Copy-Item -Path $guiShimOutput.FullName -Destination (Join-Path $toolsDirectory 'publishshim.winexe.exe') -Force
