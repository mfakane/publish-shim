# PublishShim

[日本語版](README.ja.md)

PublishShim is an MSBuild target and native launcher that replaces a published executable with a shim and relocates the actual application to another directory.

The resulting publish output looks like this:

- `MyApp.exe` - the shim
- `.app/MyApp.exe` - the actual application
- `.app/...` - the rest of the publish output

The shim runs as `MyApp.exe` and starts `.app/MyApp.exe` using the configuration embedded in the shim.

## Features

- Relocate a published application into a subdirectory
- Keep the original executable name for the shim
- Choose between a console shim and a GUI shim
- Automatically detect the target EXE's PE subsystem when `PublishShimKind=Auto`

## Requirements

- Publish for Windows
- Set a `RuntimeIdentifier`
- Import `build/PublishShim.MSBuild.targets`
- Provide `tools/<RID>/publishshim.exe` and `tools/<RID>/publishshim.winexe.exe`

## Usage

### NuGet package

The initial release is distributed as the `PublishShim.MSBuild` package with native shims for `win-x64`. Referencing the package automatically imports the MSBuild targets.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishShim>true</PublishShim>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PublishShim.MSBuild" Version="0.1.0" />
  </ItemGroup>
</Project>
```

Running `dotnet publish` with this configuration generates the shim and the `.app` directory in the publish output. The initial package supports only the `win-x64` RID.

The equivalent explicit properties are:

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <PublishShim>true</PublishShim>
  <PublishShimKind>Auto</PublishShimKind>
  <PublishShimDirectory>.app</PublishShimDirectory>
</PropertyGroup>

<Import Project="build\PublishShim.MSBuild.targets" />
```

Running `dotnet publish` then generates the shim after publishing.

## MSBuild properties

### `PublishShim`

Set this property to `true` to enable shim generation. The default is `false`.

### `PublishShimKind`

Specifies the kind of shim to generate. The default is `Auto`.

- `Auto`
  - Reads the subsystem of the published target EXE and detects the appropriate shim
  - Uses the console shim for `Windows CUI`
  - Uses the GUI shim for `Windows GUI`
- `Exe`
  - Forces the console shim
- `WinExe`
  - Forces the GUI shim

### `PublishShimDirectory`

The directory where the actual application is relocated. The default is `.app`. The path is relative to the publish root.

### `PublishShimTargetExecutableName`

The name of the target EXE to replace with a shim. It is normally detected automatically and does not need to be specified.

## Shim behavior

### Console shim (`Exe`)

- Inherits standard input, standard output, and standard error from the child process
- Waits for the child process to exit
- Returns the child's exit code unchanged

### GUI shim (`WinExe`)

- Starts the target application
- Exits immediately after the application starts successfully

### `PUBLISH_SHIM_ROOT`

When starting the target application, the shim sets these environment variables:

- `PUBLISH_SHIM_ROOT` - the absolute path to the publish root containing the shim
- `PUBLISH_SHIM_EXE` - the absolute path to the shim that was invoked

The actual application can use these values to locate the publish root and the shim even after it has been moved into `.app`.

## Advanced usage

### Referencing files next to the shim

Use `PUBLISH_SHIM_ROOT` when the actual application inside `.app` needs to access a file placed next to the shim. Files inside the application publish output should normally be resolved from `AppContext.BaseDirectory` as usual.

```csharp
var appRoot = Environment.GetEnvironmentVariable("PUBLISH_SHIM_ROOT")
    ?? AppContext.BaseDirectory;

var configPath = Path.Combine(appRoot, "config.json");
```

### Creating multiple entry points

Multiple shims can start the same actual application, which can then choose its behavior based on the shim that was invoked. The target application can identify the invoking shim through `PUBLISH_SHIM_EXE`.

```csharp
var shimExe = Environment.GetEnvironmentVariable("PUBLISH_SHIM_EXE");
var invokedAs = Path.GetFileNameWithoutExtension(shimExe);

if (string.Equals(invokedAs, "MyApp-cli", StringComparison.OrdinalIgnoreCase))
{
    RunCli();
}
else
{
    RunGui();
}
```

For example, a console-subsystem application can expose a CLI entry point that inherits standard input and output and returns the child exit code through an `Exe` shim, while a `WinExe` shim starts the same application with `CREATE_NO_WINDOW` and exits immediately. This provides CLI and GUI entry points without duplicating the actual application.

### MSBuild procedure for multiple shims

The standard `GeneratePublishShim` target calls the relocation task once and then generates the standard entry-point shim. To create multiple entry points, call `RelocatePublishArtifactsTask` once from a custom target and call `GeneratePublishShimTask` once for each entry point.

`RelocatePublishArtifactsTask` moves the publish artifacts into `ShimDirectory` and outputs the relative path to the actual application. `GeneratePublishShimTask` receives that relative path and creates a shim with the specified name and kind in the publish root. This example assumes that `PublishShim.MSBuild.targets` has already been imported.

The following example moves `MyApp.exe` to `.app\MyApp.exe` and creates `MyApp-cli.exe` and `MyApp-gui.exe`, both of which start the same actual application.

```xml
<PropertyGroup>
  <!-- Use the custom target below instead of the standard target. -->
  <PublishShim>false</PublishShim>
  <PublishShimTargetExecutableName>MyApp.exe</PublishShimTargetExecutableName>
</PropertyGroup>

<Target Name="GenerateMultiplePublishShims" AfterTargets="Publish">
  <ItemGroup>
    <_PublishShimRelocationFiles
        Include="$(PublishDir)**\*"
        Exclude="$(PublishDir)$(PublishShimDirectory)\**;$(PublishDir)MyApp-cli.exe;$(PublishDir)MyApp-gui.exe" />
  </ItemGroup>

  <!-- Relocate the publish artifacts once. -->
  <RelocatePublishArtifactsTask
      PublishDirectory="$(PublishDir)"
      Files="@(_PublishShimRelocationFiles)"
      TargetExecutableName="$(PublishShimTargetExecutableName)"
      ShimDirectory=".app">
    <Output TaskParameter="ActualApplicationRelativePath"
            PropertyName="_PublishShimActualApplicationRelativePath" />
  </RelocatePublishArtifactsTask>

  <!-- Generate shims for the same relocated application. -->
  <GeneratePublishShimTask
      PublishDirectory="$(PublishDir)"
      ShimExecutableName="MyApp-cli.exe"
      TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
      PublishShimKind="Exe"
      RuntimeIdentifier="$(RuntimeIdentifier)"
      NativeShimPath="$(PublishShimNativeShimPath)"
      NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
  <GeneratePublishShimTask
      PublishDirectory="$(PublishDir)"
      ShimExecutableName="MyApp-gui.exe"
      TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
      PublishShimKind="WinExe"
      RuntimeIdentifier="$(RuntimeIdentifier)"
      NativeShimPath="$(PublishShimNativeShimPath)"
      NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
</Target>
```

The standard `GeneratePublishShim` target includes `$(PublishDir)**\*` as relocation candidates and excludes `$(PublishDir)$(PublishShimDirectory)\**`. To leave additional files in the publish root while using the standard target, specify publish-root globs in `PublishShimRelocationExclude`.

```xml
<PropertyGroup>
  <PublishShimRelocationExclude>$(PublishDir)config.json;$(PublishDir)*-cli.exe;$(PublishDir)*-gui.exe</PublishShimRelocationExclude>
</PropertyGroup>
```

`GeneratePublishShimTask` uses `ShimExecutableName` for the generated shim file name and `TargetRelativePath` for the relative path to the actual application. Keeping these values separate allows multiple shims to point to the same application.

## Sample

`samples/PublishShim.Sample` is a minimal example.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishShim>true</PublishShim>
    <PublishShimDirectory>.app</PublishShimDirectory>
  </PropertyGroup>

  <Import Project="..\..\build\PublishShim.MSBuild.targets" />
</Project>
```

## Development

Create the development layout with:

```powershell
pwsh ./scripts/Prepare-DevLayout.ps1 -Configuration Debug
```

This script:

- Builds `PublishShim.MSBuild.Tasks`
- Builds the console native shim
- Builds the GUI native shim
- Copies the required files into `tasks/` and `tools/win-x64/`

Run the NuGet packaging and integration tests with:

```powershell
pwsh ./scripts/Test-Integration.ps1
```

The integration tests include a Native AOT publish. The Windows App SDK / WinUI 3 smoke test requires a Windows App SDK development environment and the appropriate runtime, so run it separately:

```powershell
pwsh ./scripts/Test-WinUI3-Integration.ps1
```

`samples/PublishShim.WinUI3.Sample` is an unpackaged, self-contained WinUI 3 sample. The smoke test initializes Windows App SDK through the shim and verifies that the WinUI dependencies inside `.app` can create a window.

You can also create the package with:

```powershell
dotnet pack PublishShim.MSBuild/PublishShim.MSBuild.csproj -c Release
```

The package contains `build/PublishShim.MSBuild.targets`, the task DLL, the native shims under `tools/win-x64/`, and the Japanese documentation as `README.ja.md`.

If you use Authenticode signing, append the shim configuration footer before signing the generated shim. Appending the footer after signing invalidates the signature.

## Limitations

- Requires Windows PE executables to detect the subsystem
- `PublishShimKind=Auto` requires the target EXE to be readable
- Fails when the subsystem is neither `Windows CUI` nor `Windows GUI`
