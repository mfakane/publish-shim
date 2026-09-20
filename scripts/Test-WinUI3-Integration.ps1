param(
	[string]$Configuration = 'Release',
	[string]$PackagePath = '',
	[string]$WindowsAppSdkVersion = '2.5.1'
)

$root = Split-Path -Parent $PSScriptRoot
$sampleProject = Join-Path $root 'samples/PublishShim.WinUI3.Sample/PublishShim.WinUI3.Sample.csproj'
$packageDirectory = Join-Path $root 'artifacts/packages'
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
	$PackagePath = Join-Path $packageDirectory 'PublishShim.MSBuild.0.1.0.nupkg'
}
if (-not (Test-Path $PackagePath)) {
	throw "PublishShim.MSBuild package was not found: '$PackagePath'."
}

$userPackagesDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
$cachedPackageDirectory = Join-Path $userPackagesDirectory 'publishshim.msbuild/0.1.0'
if (Test-Path $cachedPackageDirectory) {
	Remove-Item -LiteralPath $cachedPackageDirectory -Recurse -Force
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "PublishShim.WinUI3.Integration/$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $testRoot 'publish'
$markerPath = Join-Path $testRoot 'winui-marker.txt'
$errorMarkerPath = "$markerPath.error"
$nugetConfig = Join-Path $testRoot 'NuGet.Config'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
	@"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8

	dotnet restore $sampleProject --configfile $nugetConfig -p:WindowsAppSdkVersion=$WindowsAppSdkVersion
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

	dotnet publish $sampleProject -c $Configuration --no-restore --output $publishDirectory -p:WindowsAppSdkVersion=$WindowsAppSdkVersion
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

	$shimPath = Join-Path $publishDirectory 'PublishShim.WinUI3.Sample.exe'
	$applicationPath = Join-Path $publishDirectory '.app\PublishShim.WinUI3.Sample.exe'
	if (-not (Test-Path $shimPath)) { throw "WinUI 3 shim was not generated: '$shimPath'." }
	if (-not (Test-Path $applicationPath)) { throw "WinUI 3 application was not relocated: '$applicationPath'." }

	$dependencyFiles = Get-ChildItem -LiteralPath (Join-Path $publishDirectory '.app') -File -Recurse
	if ($dependencyFiles.Count -lt 2) { throw 'The WinUI 3 publish output did not retain its dependency/resource files.' }

	$previousMarker = $env:PUBLISH_SHIM_WINUI_SMOKE_MARKER
	$env:PUBLISH_SHIM_WINUI_SMOKE_MARKER = $markerPath
	try {
		$process = Start-Process -FilePath $shimPath -WorkingDirectory $publishDirectory -PassThru
		$process.WaitForExit(30000) | Out-Null
		if (-not $process.HasExited) {
			$process.Kill()
			throw 'The WinUI 3 shim did not exit after starting the smoke application.'
		}
	}
	finally {
		$env:PUBLISH_SHIM_WINUI_SMOKE_MARKER = $previousMarker
	}

	$deadline = [DateTime]::UtcNow.AddSeconds(30)
	while (-not (Test-Path $markerPath) -and [DateTime]::UtcNow -lt $deadline) {
		Start-Sleep -Milliseconds 100
	}
	if (Test-Path $errorMarkerPath) {
		throw "The WinUI 3 application reported an initialization error: $(Get-Content -Raw -LiteralPath $errorMarkerPath)"
	}
	if (-not (Test-Path $markerPath)) { throw 'The WinUI 3 application did not initialize through the shim.' }

	Write-Host 'PublishShim WinUI 3 integration smoke test passed.'
}
finally {
	if (Test-Path $testRoot) {
		Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
}
