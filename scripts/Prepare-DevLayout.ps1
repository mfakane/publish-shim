param(
	[string]$Configuration = 'Debug',
	[ValidateSet('x64', 'ARM64')]
	[string[]]$Platforms = @('x64', 'ARM64')
)

$root = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $root 'PublishShim.MSBuild.Tasks/PublishShim.MSBuild.Tasks.csproj'
$consoleShimProject = Join-Path $root 'PublishShim.Exe/PublishShim.Exe.vcxproj'
$guiShimProject = Join-Path $root 'PublishShim.WinExe/PublishShim.WinExe.vcxproj'
$tasksDirectory = Join-Path $root 'tasks'
$toolsRoot = Join-Path $root 'tools'
$nativeOutputRoot = Join-Path $root 'artifacts/native'

# MSBuild platform name, package RID, and the PE machine type its shims must carry.
$platformInfo = @{
	'x64' = @{ RuntimeIdentifier = 'win-x64'; Machine = 0x8664 }
	'ARM64' = @{ RuntimeIdentifier = 'win-arm64'; Machine = 0xAA64 }
}

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

function Get-PeMachine {
	param(
		[string]$Path
	)

	$bytes = [System.IO.File]::ReadAllBytes($Path)
	$peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
	return [int][System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

dotnet build $taskProject -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$taskOutput = Join-Path $root "PublishShim.MSBuild.Tasks/bin/$Configuration/netstandard2.0"
New-Item -ItemType Directory -Force -Path $tasksDirectory | Out-Null
Copy-Item -Path (Join-Path $taskOutput '*') -Destination $tasksDirectory -Recurse -Force

foreach ($platform in $Platforms) {
	$info = $platformInfo[$platform]
	# Each platform builds into its own directory so the copied shims cannot be
	# picked up from another platform's output.
	$outDir = (Join-Path $nativeOutputRoot "$platform/$Configuration").TrimEnd('\', '/') + '/'
	$intDir = Join-Path $nativeOutputRoot "obj/$platform/$Configuration"

	foreach ($project in @($consoleShimProject, $guiShimProject)) {
		$projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
		Invoke-NativeMsBuild @(
			$project,
			"/p:Configuration=$Configuration",
			"/p:Platform=$platform",
			"/p:OutDir=$outDir",
			"/p:IntDir=$((Join-Path $intDir $projectName).TrimEnd('\', '/'))/"
		)
		if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
	}

	$toolsDirectory = Join-Path $toolsRoot $info.RuntimeIdentifier
	New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
	foreach ($shimName in @('publishshim.exe', 'publishshim.winexe.exe')) {
		$shimOutput = Join-Path $outDir $shimName
		if (-not (Test-Path $shimOutput)) { throw "Native shim output was not found: $shimOutput" }

		$machine = Get-PeMachine $shimOutput
		if ($machine -ne $info.Machine) {
			throw ("{0} has PE machine 0x{1:X4}; expected 0x{2:X4} for {3}." -f $shimOutput, $machine, $info.Machine, $info.RuntimeIdentifier)
		}

		Copy-Item -Path $shimOutput -Destination (Join-Path $toolsDirectory $shimName) -Force
	}
}
