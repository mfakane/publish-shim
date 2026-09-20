# PublishShim

[English version](README.md)

Publish 後の実行ファイルを shim に差し替え、実体のアプリケーションを別ディレクトリへ退避するための MSBuild ターゲットとネイティブランチャーです。

最終的な publish 出力は次のようになります。

- `MyApp.exe` - shim
- `.app/MyApp.exe` - 実体のアプリケーション
- `.app/...` - その他の publish 成果物

shim は `MyApp.exe` として起動され、埋め込まれた設定から `.app/MyApp.exe` を起動します。

## できること

- publish 済みアプリをサブディレクトリへ移動する
- 元のファイル名のまま shim を配置する
- コンソールアプリ用 shim と GUI アプリ用 shim を切り替える
- `PublishShimKind=Auto` の場合、ターゲット EXE の PE subsystem から自動判定する

## 前提

- Windows 向け publish で使う
- `RuntimeIdentifier` を指定する
- `build/PublishShim.MSBuild.targets` を import する
- `tools/<RID>/publishshim.exe` と `tools/<RID>/publishshim.winexe.exe` が配置されている

## 使い方

### NuGet パッケージ

初回リリースは `win-x64` の native shim を含む `PublishShim.MSBuild` パッケージとして配布します。パッケージを参照すると MSBuild ターゲットは自動 import されます。

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

その状態で `dotnet publish` を実行すると、publish 出力に shim と `.app` ディレクトリが生成されます。初回パッケージの対応 RID は `win-x64` のみです。

`csproj` に以下を追加します。

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <PublishShim>true</PublishShim>
  <PublishShimKind>Auto</PublishShimKind>
  <PublishShimDirectory>.app</PublishShimDirectory>
</PropertyGroup>

<Import Project="build\PublishShim.MSBuild.targets" />
```

その状態で `dotnet publish` を実行すると、Publish 完了後に shim が生成されます。

## MSBuild プロパティ

### `PublishShim`

`true` のとき shim 生成を有効にします。既定値は `false` です。

### `PublishShimKind`

生成する shim の種類を指定します。既定値は `Auto` です。

- `Auto`
  - publish されたターゲット EXE の subsystem を読んで自動判定します
  - `Windows CUI` の場合はコンソール用 shim を使います
  - `Windows GUI` の場合は GUI 用 shim を使います
- `Exe`
  - コンソール用 shim を強制します
- `WinExe`
  - GUI 用 shim を強制します

### `PublishShimDirectory`

実体アプリケーションの退避先ディレクトリです。既定値は `.app` です。publish ルートからの相対パスで指定します。

### `PublishShimTargetExecutableName`

shim に差し替えるターゲット EXE 名です。通常は自動判定されるため、明示指定は不要です。

### `PublishShimIconPath`

生成するshimのアイコンソースを指定する省略可能なプロパティです。`.ico`ファイル、またはアイコンリソースを含むEXE/DLLを指定できます。省略した場合はターゲットEXEのアイコンを使います。ターゲットにアイコンリソースがない場合は、native shimの既定アイコンを使います。

絶対パスを指定するか、`$(MSBuildProjectDirectory)`からパスを組み立てます。

```xml
<PropertyGroup>
  <PublishShimIconPath>$(MSBuildProjectDirectory)\assets\myapp.ico</PublishShimIconPath>
</PropertyGroup>
```

## shim の動作

### コンソール用 shim (`Exe`)

- 標準入力・標準出力・標準エラー出力を子プロセスへ引き継ぐ
- 子プロセスの終了を待つ
- 子プロセスの終了コードをそのまま返す

### GUI 用 shim (`WinExe`)

- ターゲットアプリケーションを起動する
- 起動成功後は shim 自身は即時終了する

### `PUBLISH_SHIM_ROOT`

shim はターゲットアプリケーションを起動するとき、次の環境変数を設定します。

- `PUBLISH_SHIM_ROOT` - shim が配置されている publish ルートの絶対パス
- `PUBLISH_SHIM_EXE` - 起動元 shim 実行ファイルの絶対パス

実体アプリケーションが `.app` へ移動された後も、publish ルートや shim 自身を参照できます。

## Advanced usage

### shim の隣に置いたファイルを参照する

`.app` 内の実体アプリケーションから、ユーザーが shim の隣に配置したファイルを参照する場合は `PUBLISH_SHIM_ROOT` を使います。アプリケーション内部の publish 成果物は、通常どおり `AppContext.BaseDirectory` から解決します。

```csharp
var appRoot = Environment.GetEnvironmentVariable("PUBLISH_SHIM_ROOT")
    ?? AppContext.BaseDirectory;

var configPath = Path.Combine(appRoot, "config.json");
```

### 複数のエントリポイントを作る

同じ実体アプリケーションを複数の shim から起動し、呼び出された shim に応じて動作を切り替えられます。ターゲットアプリケーションは `PUBLISH_SHIM_EXE` から起動元を判定できます。

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

たとえば同じコンソール subsystem の実体に対して、`Exe` shim は標準入出力を引き継いで終了コードを返し、`WinExe` shim は `CREATE_NO_WINDOW` で起動してすぐ終了する、という構成にできます。これにより、実体アプリケーションを複製せずに CLI と GUI のエントリポイントを提供できます。

### 複数のshimを生成するMSBuild手順

標準の `GeneratePublishShim` ターゲットは、退避Taskを一度呼び出したあと、標準のエントリポイント用shimを生成します。複数のエントリポイントを作る場合は、カスタムターゲットから `RelocatePublishArtifactsTask` を一度呼び出し、`GeneratePublishShimTask` をエントリポイントごとに呼び出します。

`RelocatePublishArtifactsTask` はpublish成果物を `ShimDirectory` へ移動し、実体アプリケーションへの相対パスを出力します。`GeneratePublishShimTask` はその相対パスを受け取り、指定した名前と種類のshimをpublishルートへ生成します。`PublishShim.MSBuild.targets` をimport済みであることが前提です。

次の例では、`MyApp.exe` を `.app\MyApp.exe` へ退避し、同じ実体を起動する `MyApp-cli.exe` と `MyApp-gui.exe` を生成します。

```xml
<PropertyGroup>
  <!-- 標準ターゲットの代わりに下のカスタムターゲットを使う -->
  <PublishShim>false</PublishShim>
  <PublishShimTargetExecutableName>MyApp.exe</PublishShimTargetExecutableName>
</PropertyGroup>

<Target Name="GenerateMultiplePublishShims" AfterTargets="Publish">
  <ItemGroup>
    <_PublishShimRelocationFiles
        Include="$(PublishDir)**\*"
        Exclude="$(PublishDir)$(PublishShimDirectory)\**;$(PublishDir)MyApp-cli.exe;$(PublishDir)MyApp-gui.exe" />
  </ItemGroup>

  <!-- publish成果物を一度だけ退避する -->
  <RelocatePublishArtifactsTask
      PublishDirectory="$(PublishDir)"
      Files="@(_PublishShimRelocationFiles)"
      TargetExecutableName="$(PublishShimTargetExecutableName)"
      ShimDirectory=".app">
    <Output TaskParameter="ActualApplicationRelativePath"
            PropertyName="_PublishShimActualApplicationRelativePath" />
  </RelocatePublishArtifactsTask>

  <!-- 退避した同じ実体を指すshimをエントリポイントごとに生成する -->
  <GeneratePublishShimTask
      PublishDirectory="$(PublishDir)"
      ShimExecutableName="MyApp-cli.exe"
      TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
      PublishShimKind="Exe"
      IconPath="$(PublishShimIconPath)"
      RuntimeIdentifier="$(RuntimeIdentifier)"
      NativeShimPath="$(PublishShimNativeShimPath)"
      NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
  <GeneratePublishShimTask
      PublishDirectory="$(PublishDir)"
      ShimExecutableName="MyApp-gui.exe"
      TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
      PublishShimKind="WinExe"
      IconPath="$(PublishShimIconPath)"
      RuntimeIdentifier="$(RuntimeIdentifier)"
      NativeShimPath="$(PublishShimNativeShimPath)"
      NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
</Target>
```

標準の `GeneratePublishShim` ターゲットは、`$(PublishDir)**\*` を移動対象にし、`$(PublishDir)$(PublishShimDirectory)\**` を除外します。標準ターゲットのまま追加のファイルを残す場合は、publishルートを含むglobを `PublishShimRelocationExclude` に指定します。

```xml
<PropertyGroup>
  <PublishShimRelocationExclude>$(PublishDir)config.json;$(PublishDir)*-cli.exe;$(PublishDir)*-gui.exe</PublishShimRelocationExclude>
</PropertyGroup>
```

`GeneratePublishShimTask` の `ShimExecutableName` は生成するshimのファイル名、`TargetRelativePath` は実体アプリケーションへの相対パスです。両者を分けて指定するため、複数のshimが同じ実体を指せます。

## サンプル

`samples/PublishShim.Sample` は最小構成のサンプルです。

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

## 開発者向け

開発用の配置は次のスクリプトで作成できます。

```powershell
pwsh ./scripts/Prepare-DevLayout.ps1 -Configuration Debug
```

このスクリプトは以下を行います。

- `PublishShim.MSBuild.Tasks` をビルドする
- コンソール用 native shim をビルドする
- GUI 用 native shim をビルドする
- `tasks/` と `tools/win-x64/` に必要ファイルをコピーする

NuGet パッケージと統合テストは次のコマンドで実行できます。

```powershell
pwsh ./scripts/Test-Integration.ps1
```

この統合テストには Native AOT publish も含まれます。Windows App SDK / WinUI 3 の依存ファイルとリソース配置を確認する smoke test は、Windows App SDK の開発環境と runtime 条件が必要なため、次で個別に実行します。

```powershell
pwsh ./scripts/Test-WinUI3-Integration.ps1
```

`samples/PublishShim.WinUI3.Sample` は unpackaged・self-contained の WinUI 3 サンプルです。smoke test は shim 経由で Windows App SDK を初期化し、`.app` 内の WinUI 依存ファイルを使ってウィンドウを生成できることを確認します。

`dotnet pack PublishShim.MSBuild/PublishShim.MSBuild.csproj -c Release` でもパッケージを生成できます。パッケージには `build/PublishShim.MSBuild.targets`、task DLL、`tools/win-x64/` の native shim が含まれます。

Authenticode 署名を行う場合は、shim の設定フッターを付加した後に生成済み shim を署名してください。署名後にフッターを追加すると署名が無効になります。

## 制限

- Windows PE 実行ファイルを前提に subsystem を判定します
- `PublishShimKind=Auto` はターゲット EXE を読めることが前提です
- subsystem が `Windows CUI` / `Windows GUI` 以外の場合はエラーになります
