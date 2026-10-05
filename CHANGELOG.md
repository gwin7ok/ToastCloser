# Changelog

All notable changes to this project will be documented in this file.

The format is based on "Keep a Changelog" (https://keepachangelog.com/en/1.0.0/)
and this project adheres to Semantic Versioning.

## [1.4.0] - 2026-10-05

### Changed

- IME 変換中の無効化状態管理を WndProc ベースに変更しました。
- 外部 IME 検出（ATOK、Google 日本語入力、MS-IME）を追加しました。
- UIA 開閉探索をスキップして、ショートカット送信を即実行するようにしました。
- ポーリング間隔を調整しました（監視ループの遅延を 500ms から 1000ms に変更）。
- キー状態チェックを全キー走査に変更し、フラグを一括クリアして最後に 1 度だけ更新するようにしました。
- キー入力検知の対象から vk=243,244（0xF3/0xF4）を除外し、vk=242（0xF2、カタカナひらがな）のみ対象としました。キー操作がなくても一部アプリ（VS Code、Claude デスクトップなど）から継続的に検知され、ショートカット送信が抑制され続ける問題を修正しました。

## [1.3.0] - 2026-08-25

### Changed

- キーボード入力検知の改善と無効化状態の管理を強化

## [1.2.0] - 2026-03-07

### Changed

- 無効化の挙動を変更しました: 個別の送信をスキップするのではなく、ポーリング（スキャン）ループレベルで停止するようにし、停止中はトラッキング状態をクリアしてワーカーが送信しないようにしました。
- 外部制御用スクリプト `toggle_feature.ps1` を実行ファイルと同じフォルダへ配置するようにしました。

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

