# PublishShim

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

## shim の動作

### コンソール用 shim (`Exe`)

- 標準入力・標準出力・標準エラー出力を子プロセスへ引き継ぐ
- 子プロセスの終了を待つ
- 子プロセスの終了コードをそのまま返す

### GUI 用 shim (`WinExe`)

- ターゲットアプリケーションを起動する
- 起動成功後は shim 自身は即時終了する

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

## 制限

- Windows PE 実行ファイルを前提に subsystem を判定します
- `PublishShimKind=Auto` はターゲット EXE を読めることが前提です
- subsystem が `Windows CUI` / `Windows GUI` 以外の場合はエラーになります
