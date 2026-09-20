param(
	[string]$Configuration = 'Release',
	[switch]$SkipPack
)

$root = Split-Path -Parent $PSScriptRoot
$packageProject = Join-Path $root 'PublishShim.MSBuild/PublishShim.MSBuild.csproj'
$testProject = Join-Path $root 'tests/PublishShim.IntegrationTests/PublishShim.IntegrationTests.csproj'
$packageDirectory = Join-Path $root 'artifacts/packages'
$packageVersion = '0.1.0'

$userPackagesDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
$cachedPackageDirectory = Join-Path $userPackagesDirectory "publishshim.msbuild/$packageVersion"
if (Test-Path $cachedPackageDirectory) {
	Remove-Item -LiteralPath $cachedPackageDirectory -Recurse -Force
}

if (-not $SkipPack) {
	dotnet pack $packageProject -c $Configuration
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$packagePath = Join-Path $packageDirectory "PublishShim.MSBuild.$packageVersion.nupkg"
if (-not (Test-Path $packagePath)) {
	throw "PublishShim.MSBuild package was not found in '$packageDirectory'."
}

dotnet run --project $testProject -c $Configuration -- $packagePath
exit $LASTEXITCODE
