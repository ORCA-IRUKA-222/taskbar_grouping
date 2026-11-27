#!/usr/bin/env bash
# 最新版を取得し、起動中のアプリを終了してからビルドし、起動し直す(Git Bash 用)
set -e
cd "$(dirname "$0")"
git pull
taskkill //F //IM TaskbarGrouping.App.exe >/dev/null 2>&1 || true
# 完全に終了するまで待つ(ファイルのロック解除待ち)
for i in 1 2 3 4 5 6 7 8 9 10; do
  tasklist | grep -qi TaskbarGrouping.App.exe || break
  sleep 1
done
dotnet publish src/TaskbarGrouping.App -c Release -r win-x64 --self-contained false -o publish
./publish/TaskbarGrouping.App.exe >/dev/null 2>&1 &
echo "ビルドして起動しました(ビルド日時は error.log の「起動開始 build=」で確認できます)"
