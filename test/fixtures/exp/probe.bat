@echo off
setlocal EnableDelayedExpansion
set "BIN=C:\exp\bin\"
set "LISTS=C:\exp\lists\"
set "args="
set "capture=0"
set "mergeargs=0"
for /f "tokens=*" %%a in ('type "%~dp0strat.bat"') do (
  set "line=%%a"
  call set "line=!line!"
  echo CHECK: !line! | findstr /i "winws.exe" >nul
  if not errorlevel 1 (
    set "capture=1"
  )
  if !capture!==1 (
    if not defined args set "line=!line:*winws.exe"=!"
    echo AFTER: [!line!]
  )
)
