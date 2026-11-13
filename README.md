# Taskbar Grouping

標準の Windows タスクバーを拡張し、ユーザーが選んだ複数アプリを 1 つのボタンにまとめる常駐ツール。

## 仕組み
タスクバーは AppUserModelID(AUMID)が同じウィンドウを 1 ボタンにまとめる。
常駐アプリが、グループに登録されたプロセスのウィンドウへ共通の AUMID を設定する。
独自バーは作らない。終了時は AUMID を戻してグループ化を解除する。

## ビルド(Windows, .NET 8 SDK)
    dotnet build src/TaskbarGrouping.App
    dotnet run --project src/TaskbarGrouping.App

設定は `%APPDATA%\TaskbarGrouping\groups.json` に保存。

## 未検証・既知の制約
- 未ビルド・未実機検証(開発環境が Linux で .NET SDK なし)。
- 他プロセスのウィンドウへ AUMID を設定できるかは Windows 上で要確認。
- ボタンのアイコン・名前は最初のウィンドウのものになる。
- ストア(UWP)アプリは ApplicationFrameHost 配下のため対象外になる可能性あり。
- 起動済みのウィンドウは再グループ化で一瞬ボタンが入れ替わる。
