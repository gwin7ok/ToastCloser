# noticeWindowFinder (Chrome YouTube 通知自動閉鎖)

## Overview

- **リポジトリ**: `noticeWindowFinder`
- **主な目的**: Windows 上で表示される通知（トースト）を検出し、YouTube 関連の通知を自動で処理（既定は通知を履歴へ移動または閉じる）するデスクトップユーティリティ `ToastCloser` の実装とツール群を含みます。

## ToastCloser の概要

- **言語/ランタイム**: C# / .NET 8 ( `net8.0-windows` )
- **UI 自動化**: `FlaUI` を使用して Windows の通知 UI を検出・操作します。
- **動作モード**: トレイアプリケーションとして単一インスタンスで動作し、既定は「履歴に移す（Notification Center を開く）」方式です（`preserveHistory`）。
- **設定**: 実行ディレクトリに置かれる `ToastCloser.ini`（`Config` クラスで読み書き）で各種パラメータを変更できます（`DisplayLimitSeconds`、`PollIntervalSeconds`、`YoutubeOnly` など）。

## ビルド要件

- **.NET SDK**: .NET 8 SDK が必要です。`dotnet --version` で確認してください。
- **依存パッケージ**: `FlaUI.Core` / `FlaUI.UIA3` が `csproj` に定義されています。`dotnet restore` で自動取得されます。

## ビルドと実行（ローカル開発）

1. ルートから Visual Studio でソリューションを開くか、PowerShell で以下を実行:

```powershell
# csharp/ToastCloser をビルド
dotnet build .\csharp\ToastCloser\ToastCloser.csproj -c Debug

# デバッグ実行（コンソール版が開きます）
dotnet run --project .\csharp\ToastCloser\ToastCloser.csproj --configuration Debug
```

注意: ビルドは `Resources/ToastCloser.ico` が既に存在することを前提としています。リポジトリ付属の PNG から ICO を生成するには次のスクリプトを事前に実行してください:

```powershell
# リポジトリルートから実行
pwsh -File .\scripts\generate-ico.ps1
```

1. 設定をファイルで変更する場合は、実行後に生成される `ToastCloser.ini` を編集してください。

## トレイ（GUI）で常駐させる

- `ToastCloser` はトレイアプリとして動作します。通常は単独実行でトレイアイコンが表示され、設定ウィンドウやログ出力を利用できます。

## リリース用単一実行ファイル（Windows x64）

ワークスペースに VS Code タスクが用意されています（例: `Publish: (Release win-x64 single-file)`）。手動で行う場合:

```powershell
dotnet publish .\csharp\ToastCloser\ToastCloser.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish\ToastCloser\win-x64
```

注: `ToastCloser.csproj` はアイコン生成や実験的バンドルパラメータを含んでいます。単一ファイル化は実験的なネイティブ依存取り込みに影響するため、必要に応じて `csproj` の条件を確認してください。

## 設定項目（主なもの）

- `DisplayLimitSeconds`: 通知を処理対象とする表示秒数の閾値（デフォルト 10）
- `PollIntervalSeconds`: UI 検出ポーリング間隔（秒）
- `YoutubeOnly`: true の場合、YouTube に由来する通知のみ処理対象
- `VerboseLog`: true にするとデバッグログを詳細に出力
- 設定ファイル: 実行ディレクトリの `ToastCloser.ini`（`Config.Load()` / `Config.Save()` で読み書き）

## ログ

- 実行ディレクトリの `logs/auto_closer.log` に出力されます。起動時にローテーションされ、古いログは `Config.LogArchiveLimit` に従い削除されます。

## トラブルシューティング

- UI 自動化は「同一ユーザーのデスクトップセッション」でのみ有効です。サービスや別ユーザーのセッションからは操作できません。
- 管理者権限で実行されているアプリや UAC により制限されたプロセスは操作できない場合があります。
- UI 検出のタイミングや名称は Windows のローカライズや OS バージョンで変わることがあるため、問題がある場合は `logs/` の出力を確認してください。

## 開発用ツールと補助スクリプト

- `tools/` ディレクトリに小さな補助ツール（アイコン生成やテスト用実行）が置かれています。
- VS Code のタスク (`.vscode/tasks.json`) にビルド / publish / 実行用のエントリが用意されています。タスク名: `dotnet: build ToastCloser`, `Publish: (Release win-x64 single-file)` など。

## 更新履歴 (Changelog)

- このリポジトリの更新履歴は `CHANGELOG.md` にまとめています。README 内で長く履歴を載せず、リリースノートはそちらを参照してください。
- リリースページ用の説明文は `CHANGELOG.md` から生成できます。ローカルで最新リリースの説明を作るには次のスクリプトを使います:

```powershell
# 例: 最新タグのセクションを `release-body.md` に書き出す
pwsh -File .\scripts\generate-release-body.ps1 -OutFile release-body.md

# 生成した `release-body.md` を使って gh CLI でリリースを作成する例:
gh release create <tag> --title <tag> --notes-file release-body.md --repo <owner>/<repo>
```

## 貢献 / ライセンス

- このリポジトリは個人的なユーティリティを収めています。変更を加える場合はコードの動作を理解した上でプルリクエストを送ってください。

---

この README は `ToastCloser` の最新の挙動とワークフローに合わせて更新しました。追加で入れたい使用例やスクリーンショット、配布方法があれば教えてください。
