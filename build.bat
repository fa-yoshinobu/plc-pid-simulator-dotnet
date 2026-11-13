@echo off
setlocal

rem PID Process Simulator のビルド
rem   build.bat          テストを実行してから、単一ファイルの exe を作成する
rem   build.bat notest   テストを省略して exe を作成する

chcp 65001 >nul

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%PidSimulator.sln"
set "PROJECT=%ROOT%src\PidSimulator.App\PidSimulator.App.csproj"
set "TESTS=%SOLUTION%"
set "PUBLISH_DIR=%ROOT%artifacts\release"
set "EXE_NAME=PidSimulator.exe"
set "EXE=%PUBLISH_DIR%\%EXE_NAME%"

echo [build] PID Process Simulator single-file publish
echo [build] Project: "%PROJECT%"
echo [build] Output : "%EXE%"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [error] dotnet が見つかりません。.NET 10 SDK をインストールしてください。
  echo [error] https://dotnet.microsoft.com/download/dotnet/10.0
  exit /b 1
)

if /i "%~1"=="notest" (
  echo [build] テストを省略します。
) else (
  echo [build] テストを実行しています...
  dotnet test "%TESTS%" -c Release --nologo -v q
  if errorlevel 1 (
    echo [error] テストに失敗しました。exe は作成しません。
    exit /b 1
  )
)

if exist "%PUBLISH_DIR%" (
  tasklist /FI "IMAGENAME eq %EXE_NAME%" 2>nul | find /I "%EXE_NAME%" >nul
  if not errorlevel 1 (
    echo [error] %EXE_NAME% が起動しています。アプリを終了してから実行してください。
    exit /b 1
  )
)

echo [build] exe を作成しています...
dotnet publish "%PROJECT%" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o "%PUBLISH_DIR%" ^
  --nologo ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false

if errorlevel 1 (
  echo [error] publish に失敗しました。
  exit /b 1
)

if not exist "%EXE%" (
  echo [error] 単一ファイルの exe が作成されていません。
  exit /b 1
)

powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; $zip = Join-Path $env:PUBLISH_DIR 'PidSimulator-win-x64.zip'; Compress-Archive -LiteralPath @((Join-Path $env:PUBLISH_DIR 'PidSimulator.exe'),(Join-Path $env:PUBLISH_DIR 'DEMO.psim')) -DestinationPath $zip -Force; $line = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($zip)))).Replace('-', '').ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) + [char]10; [IO.File]::WriteAllText($zip + '.sha256', $line, [Text.UTF8Encoding]::new($false))"
if errorlevel 1 exit /b 1

echo [build] 完了: "%EXE%"
exit /b 0
