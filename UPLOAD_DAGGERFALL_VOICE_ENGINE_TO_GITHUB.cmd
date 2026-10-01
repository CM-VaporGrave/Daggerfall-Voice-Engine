@echo off
setlocal EnableExtensions
title Upload Daggerfall Voice Engine to GitHub
cd /d "%~dp0"

set "REPO_URL=https://github.com/CM-VaporGrave/Daggerfall-Voice-Engine.git"

echo ============================================================
echo  Daggerfall Voice Engine - GitHub uploader
echo ============================================================
echo.
echo Put this CMD in the ROOT of your finished Voice Engine folder.
echo Then double-click it.
echo.
pause

where git >nul 2>&1
if errorlevel 1 (
  echo ERROR: Git is not installed or not available in PATH.
  echo Install Git for Windows, then run this file again.
  pause
  exit /b 1
)

git lfs version >nul 2>&1
if errorlevel 1 (
  echo ERROR: Git LFS is not installed.
  echo Install Git LFS, then run this file again.
  pause
  exit /b 1
)

if not exist ".git" git init
git branch -M main

git remote get-url origin >nul 2>&1
if errorlevel 1 (
  git remote add origin "%REPO_URL%"
) else (
  git remote set-url origin "%REPO_URL%"
)

git lfs install
git lfs track "*.onnx"
git lfs track "*.npy"
git lfs track "*.bin"

if not exist ".gitignore" (
  >".gitignore" (
    echo .vs/
    echo obj/
    echo *.user
    echo *.suo
    echo *.tmp
    echo *.log
    echo Cache/
    echo cache/
    echo Generated/
    echo generated/
    echo Thumbs.db
    echo .DS_Store
  )
)

for /f "delims=" %%A in ('git config user.name 2^>nul') do set "GITNAME=%%A"
if not defined GITNAME (
  set /p "GITNAME=Enter the name to show on GitHub commits: "
  git config user.name "%GITNAME%"
)

for /f "delims=" %%A in ('git config user.email 2^>nul') do set "GITEMAIL=%%A"
if not defined GITEMAIL (
  set /p "GITEMAIL=Enter the email associated with your GitHub account: "
  git config user.email "%GITEMAIL%"
)

git add .gitattributes
git add .

echo.
echo Files handled by Git LFS:
git lfs ls-files
echo.

git diff --cached --quiet
if errorlevel 1 (
  git commit -m "Daggerfall Voice Engine v1.0.2"
)

echo.
echo Uploading to GitHub...
echo A browser sign-in window may appear.
git push -u origin main

if errorlevel 1 (
  echo.
  echo PUSH FAILED.
  echo Copy the error above and send it to ChatGPT.
  echo Do NOT force-push.
  pause
  exit /b 1
)

echo.
echo SUCCESS.
echo Repository:
echo %REPO_URL%
pause
