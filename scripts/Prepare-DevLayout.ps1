param(
	[string]$Configuration = 'Debug'
)

$root = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $root 'PublishShim.MSBuild.Tasks/PublishShim.MSBuild.Tasks.csproj'
$consoleShimProject = Join-Path $root 'PublishShim.Exe/PublishShim.Exe.vcxproj'
$guiShimProject = Join-Path $root 'PublishShim.WinExe/PublishShim.WinExe.vcxproj'
$tasksDirectory = Join-Path $root 'tasks'
$toolsDirectory = Join-Path $root 'tools/win-x64'

function Invoke-NativeMsBuild {
	param(
		[string[]]$Arguments
	)

	$msbuildCommand = Get-Command msbuild -ErrorAction SilentlyContinue
	if ($msbuildCommand) {
		& $msbuildCommand.Source @Arguments
		return
	}

	$vswhereCandidates = @(
		'C:\Program Files\Microsoft Visual Studio\Installer\vswhere.exe',
		'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
	)
	$vswhere = $vswhereCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
	if ($vswhere) {
		$msbuildPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
		if (-not $msbuildPath) {
			$msbuildPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
		}
		if ($msbuildPath -and (Test-Path $msbuildPath)) {
			& $msbuildPath @Arguments
			return
		}
	}

	throw 'Visual Studio MSBuild with C++ tools is required to build the native shims.'
}

dotnet build $taskProject -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Invoke-NativeMsBuild @(
	$consoleShimProject,
	"/p:Configuration=$Configuration",
	'/p:Platform=x64'
)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Invoke-NativeMsBuild @(
	$guiShimProject,
	"/p:Configuration=$Configuration",
	'/p:Platform=x64'
)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$taskOutput = Join-Path $root "PublishShim.MSBuild.Tasks/bin/$Configuration/netstandard2.0"
$consoleShimOutput = Get-ChildItem -Path $root -Recurse -Filter 'publishshim.exe' | Where-Object { $_.FullName -like "*$Configuration*" } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$guiShimOutput = Get-ChildItem -Path $root -Recurse -Filter 'publishshim.winexe.exe' | Where-Object { $_.FullName -like "*$Configuration*" } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $consoleShimOutput) { throw 'Console native shim output was not found.' }
if (-not $guiShimOutput) { throw 'GUI native shim output was not found.' }

New-Item -ItemType Directory -Force -Path $tasksDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
Copy-Item -Path (Join-Path $taskOutput '*') -Destination $tasksDirectory -Recurse -Force
Copy-Item -Path $consoleShimOutput.FullName -Destination (Join-Path $toolsDirectory 'publishshim.exe') -Force
Copy-Item -Path $guiShimOutput.FullName -Destination (Join-Path $toolsDirectory 'publishshim.winexe.exe') -Force
