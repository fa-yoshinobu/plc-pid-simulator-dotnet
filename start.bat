@echo off
setlocal

chcp 65001 >nul

set "ROOT=%~dp0"
set "EXE=%ROOT%artifacts\release\PidSimulator.exe"

if not exist "%EXE%" (
  echo [error] exe がありません: "%EXE%"
  echo [hint] 先に build.bat を実行してください。
  exit /b 1
)

echo [start] "%EXE%"
start "" "%EXE%"
exit /b 0
