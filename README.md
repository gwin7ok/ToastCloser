# ToastCloser (Chrome YouTube 通知自動閉鎖)

## 概要

- **リポジトリ**: `ToastCloser`
- **目的**: Windows 上で表示される通知（トースト）を検出し、YouTube 関連の通知を自動で処理するデスクトップユーティリティ `ToastCloser` を含みます。

## 主要技術

- 言語/ランタイム: C# / .NET 8 (`net8.0-windows`)
- UI 自動化: `FlaUI` を利用

## 簡単な使い方

- 配布版（推奨）: GitHub Releases から配布されているアーカイブをダウンロードして解凍し、含まれる `ToastCloser.exe` を実行してください。

  例:

  1. Releases: https://github.com/gwin7ok/ToastCloser/releases
  2. 最新の `ToastCloser_*_win-x64.zip` をダウンロードして解凍。
  3. 解凍フォルダ内の `ToastCloser.exe` をダブルクリックして起動。

  - 短い注意: `ToastCloser` はデスクトップ上の通知 UI を操作するため、同一ユーザーのデスクトップセッションで実行してください。必要に応じて管理者権限で実行してください。

外部からのトグル操作 (toggle_feature.ps1)

- 配布版では `toggle_feature.ps1` が実行ファイルと同じフォルダにコピーされます。
- 役割: 実行中の `ToastCloser` に対して named pipe 経由で `toggle` / `disable` / `enable` コマンドを送信し、機能の有効/無効を切り替えます。
- 使い方（配布フォルダ内から呼ぶ例）:

```powershell
& '.\toggle_feature.ps1' toggle
```
- オプション: `toggle` / `disable` / `enable` のいずれかを指定できます。
- 注意:
  - パスに空白が含まれる場合は必ずクォートしてください。
  - 新しい PowerShell プロセスで実行したい場合は次のようにします:

```powershell
pwsh -NoProfile -File ".\toggle_feature.ps1" toggle
```


## ローカルでビルド（開発者向け）

1. .NET 8 SDK を用意します。
2. ルートで次を実行:

```powershell
dotnet build .\csharp\ToastCloser\ToastCloser.csproj -c Debug
dotnet run --project .\csharp\ToastCloser\ToastCloser.csproj --configuration Debug
```

注: `Resources/ToastCloser.ico` が必要です。リポジトリの PNG から ICO を生成するには:

```powershell
pwsh -File .\scripts\generate-ico.ps1
## ToastCloser — Chrome/YouTube 通知の自動処理ユーティリティ

Windows デスクトップに表示される通知（トースト）を検出し、YouTube に関連する通知を自動で閉じたり履歴へ移すなどの操作を行うデスクトップユーティリティです。

短い紹介:

- **プロジェクト名**: `ToastCloser`
- **リポジトリ**: `gwin7ok/ToastCloser` (https://github.com/gwin7ok/ToastCloser)
- **目的**: デスクトップ通知を自動で検出・操作し、不要な YouTube 通知を自動で処理する

主要技術:

- 言語/ランタイム: C# / .NET 8 (`net8.0-windows`)
- UI 自動化: `FlaUI` を利用して Windows の通知 UI を検出・操作

配布版の利用方法 (推奨)

1. GitHub Releases から最新のアーカイブをダウンロードします: https://github.com/gwin7ok/ToastCloser/releases
2. ZIP を解凍して `ToastCloser.exe` を実行してください（同一ユーザーのデスクトップセッションで実行する必要があります）。

注意:

- 通知 UI を操作するため、同一ユーザーのデスクトップセッションで実行してください（サービスや別ユーザーのセッションからは操作できません）。
- 必要に応じて管理者権限で実行してください。

ローカルでビルド（開発者向け）

前提: .NET 8 SDK がインストールされていること。

ルートからビルドと実行の例:

```powershell
dotnet build .\csharp\ToastCloser\ToastCloser.csproj -c Debug
dotnet run --project .\csharp\ToastCloser\ToastCloser.csproj --configuration Debug
```

アイコンが必要な場合（`Resources/ToastCloser.ico`）は、リポジトリ付属の PNG から生成するスクリプトを利用できます:

```powershell
pwsh -File .\scripts\generate-ico.ps1
```

主な設定項目

- `DisplayLimitSeconds`: 通知を処理対象とする表示秒数の閾値（デフォルト 10）
- `PollIntervalSeconds`: UI 検出ポーリング間隔（秒）
- `YoutubeOnly`: true の場合、YouTube に由来する通知のみ処理する
- `VerboseLog`: true にするとデバッグログを詳細に出力

設定ファイル: 実行ディレクトリの `ToastCloser.ini`（`Config.Load()` / `Config.Save()` で読み書きされます）

ログ

- 実行ディレクトリの `logs/auto_closer.log` に出力されます。起動時にローテーションされ、古いログは `Config.LogArchiveLimit` に従い削除されます。

トラブルシューティング

- UI 自動化は「同一ユーザーのデスクトップセッション」でのみ有効です。サービスや別ユーザーのセッションからは操作できません。
- 管理者権限が必要なケースや UAC による制限がある場合、対象ウィンドウを操作できないことがあります。
- 問題が発生したら `logs/` の出力を確認してください。

開発用スクリプトとツール

- `scripts/` にビルドやリリースを補助するスクリプトが含まれます（例: `generate-ico.ps1`、`release-and-publish.ps1`）。
- `tools/` 配下には補助ツール（アイコン生成やテスト用ツール）が置かれています。
- VS Code 用タスク（`.vscode/tasks.json`）にビルド／publish／実行用のエントリがあります。タスク例: `dotnet: build ToastCloser`、`Publish: (Release win-x64 single-file)`。

インストール（リリースを使う）

GitHub Releases にて配布されている ZIP をダウンロードして解凍し、中の `ToastCloser.exe` を実行してください。

貢献 / ライセンス

- 個人のユーティリティとして管理しています。変更を加える場合は動作を理解した上でプルリクエストをお送りください。

更新履歴は `CHANGELOG.md` を参照してください。

---

さらに追加したい使用例、スクリーンショット、または配布手順があれば教えてください。
