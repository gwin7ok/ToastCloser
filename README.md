# noticeWindowFinder (Chrome YouTube 通知自動閉鎖)

このリポジトリは、Windows 上で表示される Google Chrome のデスクトップ（トースト）通知を検出し、指定秒数以上表示されている YouTube 通知を自動で閉じる簡易サービスのサンプル実装です。

概要:
- `pywinauto` (UIA backend) を使ってトーストウィンドウ（ClassName=`FlexibleToastView`, AutomationId=`PriorityToastView`）を検出します。
- 発見からの経過時間が閾値（デフォルト 10 秒）を超えたウィンドウの子ボタン `閉じる` をクリックして閉じます。

注意:
- スクリプトは「同一ユーザーのデスクトップセッション」で実行してください。サービス／他ユーザーのセッションでは UI を操作できません。
- UAC（昇格）の状態が異なるプロセスは操作できない場合があります（管理者権限で Chrome が動いている場合など）。

セットアップ:

PowerShell (pwsh) で次を実行します:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

実行方法:

```powershell
python src\main.py --display-limit-seconds 10 --poll-interval-seconds 1.0
```

バックグラウンド起動（PowerShell 例）:

```powershell
Start-Process -FilePath python -ArgumentList 'src\main.py --display-limit-seconds 10 --poll-interval-seconds 1.0' -WindowStyle Hidden
```

ログは標準出力に出ます。問題があればログを貼ってください。
