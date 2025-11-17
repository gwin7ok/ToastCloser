# ToastCloser (C# サンプル)

このフォルダは .NET Framework (net48) のコンソールアプリケーションのサンプルです。
System.Windows.Automation を使って Chrome のトースト通知（FlexibleToastView / PriorityToastView）を検出し、子の「閉じる」ボタンを Invoke、失敗時は WM_CLOSE を送る試みを行います。

ビルド方法 (Visual Studio / Visual Studio Build Tools / VS Code):

- Visual Studio の場合: `csharp/ToastCloser` を開いてビルド & 実行してください。
- Visual Studio Build Tools (msbuild) がある場合: コマンドラインで次を実行できます:

```powershell
msbuild csharp\ToastCloser\ToastCloser.csproj /p:Configuration=Debug
```

- VS Code の場合: ルートワークスペースに追加済みのタスクを使えます。
  - コマンドパレット (Ctrl+Shift+P) -> `Tasks: Run Build Task` で `MSBuild: build ToastCloser` を選ぶ。
  - もし `msbuild` がパスに無ければ、`dotnet: build ToastCloser` タスクを試してください（ただしこのプロジェクトは .NET Framework 4.8 をターゲットにしているので `dotnet build` は環境によって失敗する可能性があります）。

実行方法:
```powershell
# ビルド後に出力フォルダから実行（引数は順に: <minSeconds> <maxSeconds> <pollSeconds>）
.\csharp\ToastCloser\bin\Debug\ToastCloser.exe 10 30 1
```

VS Code タスク一覧に関して:
- ルートで `Terminal -> Run Task...` を開くと `MSBuild: build ToastCloser`, `dotnet: build ToastCloser`, `Run: ToastCloser` が出ます。

注意:
- このサンプルは .NET Framework をターゲットにしています。もし `msbuild` が見つからない場合は Visual Studio Build Tools をインストールしてください。
- 実行は通知と同一ユーザーのデスクトップ (ログオンセッション) で行ってください。
# ToastCloser (C# サンプル)

このフォルダは .NET Framework (net48) のコンソールアプリケーションのサンプルです。
System.Windows.Automation を使って Chrome のトースト通知（FlexibleToastView / PriorityToastView）を検出し、子の「閉じる」ボタンを Invoke、失敗時は WM_CLOSE を送る試みを行います。

ビルド方法 (Visual Studio / msbuild):
- Visual Studio で `csharp/ToastCloser` フォルダを開き、プロジェクトをビルドして実行してください。
- またはコマンドラインから MSBuild を使ってビルドできます（Visual Studio Build Tools が必要）。

実行方法:
```powershell
# 引数は順に: <minSeconds> <maxSeconds> <pollSeconds>
.
ToastCloser.exe 10 30 1
```

注意:
- このサンプルは .NET Framework をターゲットにしているため、`dotnet run` でのビルドはできない場合があります。Visual Studio を使うか、msbuild を利用してください。
- 実行は通知と同一ユーザーのデスクトップ (ログオンセッション) で行ってください。
