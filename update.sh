#!/usr/bin/env bash
# 最新版を取得し、起動中のアプリを終了してからビルドし、起動し直す(Git Bash 用)
set -e
cd "$(dirname "$0")"
git pull
taskkill //F //IM TaskbarGrouping.App.exe >/dev/null 2>&1 || true
sleep 1
dotnet publish src/TaskbarGrouping.App -c Release -r win-x64 --self-contained false -o publish
./publish/TaskbarGrouping.App.exe >/dev/null 2>&1 &
echo "起動しました"
