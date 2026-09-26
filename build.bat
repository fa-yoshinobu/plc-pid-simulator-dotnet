@echo off
setlocal

rem PID Process Simulator のビルド
rem   build.bat          テストを実行してから、単一ファイルの exe を作成する
rem   build.bat notest   テストを省略して exe を作成する

chcp 65001 >nul

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%PidSimulator.sln"
set "PROJECT=%ROOT%src\PidSimulator.App\PidSimulator.App.csproj"
set "TESTS=%ROOT%tests\PidSimulator.Tests\PidSimulator.Tests.csproj"
set "PUBLISH_DIR=%ROOT%publish\win-x64-single"
set "EXE_NAME=PidSimulator.exe"
set "EXE=%PUBLISH_DIR%\%EXE_NAME%"

echo [build] PID Process Simulator single-file publish
echo [build] Project: "%PROJECT%"
echo [build] Output : "%EXE%"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [error] dotnet が見つかりません。.NET 9 SDK をインストールしてください。
  echo [error] https://dotnet.microsoft.com/download/dotnet/9.0
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
  rmdir /s /q "%PUBLISH_DIR%"
  if exist "%PUBLISH_DIR%" (
    echo [error] 出力フォルダを削除できません。フォルダを開いているアプリやエクスプローラーを閉じてください:
    echo [error] "%PUBLISH_DIR%"
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

for /f "delims=" %%F in ('dir /b /a-d "%PUBLISH_DIR%"') do (
  if /i not "%%F"=="%EXE_NAME%" if /i not "%%F"=="DEMO.psim" (
    echo [build] 不要なファイルを削除: %%F
    del /f /q "%PUBLISH_DIR%\%%F"
  )
)

for /d %%D in ("%PUBLISH_DIR%\*") do (
  echo [build] 不要なフォルダを削除: %%~nxD
  rmdir /s /q "%%D"
)

for /f "delims=" %%F in ('dir /b "%PUBLISH_DIR%"') do (
  if /i not "%%F"=="%EXE_NAME%" if /i not "%%F"=="DEMO.psim" (
    echo [error] 出力に exe と DEMO.psim 以外のファイルがあります: %%F
    exit /b 1
  )
)

if not exist "%PUBLISH_DIR%\DEMO.psim" (
  echo [error] DEMO.psim がありません。
  exit /b 1
)

echo [build] 完了: "%EXE%"
exit /b 0
