# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog" (https://keepachangelog.com/en/1.0.0/)
and this project adheres to Semantic Versioning.

## [Unreleased]

### Added

- (none)

## [1.1.0] - 2026-03-05

### Changed

- トレイアイコンの「無効」状態を追加し、切替時にアイコンが切り替わるようにしました。
- 無効状態用アイコン `ToastCloser_disabled.ico` を埋め込みリソースとして管理するようにしました。
- 左クリックでのトグル動作をダブルクリック判定時間を待って実行するように変更し、誤ってダブルクリックでトグルされる問題を修正しました。


## [v1.0.0] - 2025-11-22

### Added

- 初期リリース

[Unreleased]: https://github.com/gwin7ok/ToastCloser/compare/v1.0.0...HEAD
[v1.0.0]: https://github.com/gwin7ok/ToastCloser/releases/tag/v1.0.0

## [1.2.0] - 2026-03-07

### Changed

- 無効化の挙動を変更しました: 個別の送信をスキップするのではなく、ポーリング（スキャン）ループレベルで停止するようにし、停止中はトラッキング状態をクリアしてワーカーが送信しないようにしました。
- 外部制御用スクリプト `toggle_feature.ps1` を実行ファイルと同じフォルダへ配置するようにしました。

