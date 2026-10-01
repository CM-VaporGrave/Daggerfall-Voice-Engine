@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Resume Daggerfall Voice Engine GitHub Upload
cd /d "%~dp0"

set "REPO_URL=https://github.com/CM-VaporGrave/Daggerfall-Voice-Engine.git"

echo ============================================================
echo  Daggerfall Voice Engine - Resume GitHub Upload
echo ============================================================
echo.
echo This fixes the failed first upload.
echo Your files and Git LFS tracking can stay exactly where they are.
echo.
pause

where git >nul 2>&1
if errorlevel 1 (
  echo ERROR: Git is not installed or not available in PATH.
  pause
  exit /b 1
)

if not exist ".git" (
  echo ERROR: No .git folder was found here.
  echo Put this CMD in the same Voice Engine folder where you ran
  echo the previous uploader.
  pause
  exit /b 1
)

echo.
echo [1/5] Setting Git author identity...
for /f "delims=" %%A in ('git config user.name 2^>nul') do set "GITNAME=%%A"
if not defined GITNAME (
  set /p "GITNAME=Name to show on commits [CM-VaporGrave]: "
  if "!GITNAME!"=="" set "GITNAME=CM-VaporGrave"
  git config user.name "!GITNAME!"
)

for /f "delims=" %%A in ('git config user.email 2^>nul') do set "GITEMAIL=%%A"
if not defined GITEMAIL (
  echo.
  echo Enter the email you want attached to this Git commit.
  echo You can use the email on your GitHub account or your GitHub
  echo no-reply email from GitHub Settings ^> Emails.
  set /p "GITEMAIL=Email: "
  if "!GITEMAIL!"=="" (
    echo ERROR: Email cannot be blank.
    pause
    exit /b 1
  )
  git config user.email "!GITEMAIL!"
)

echo.
echo Git author:
git config user.name
git config user.email

echo.
echo [2/5] Confirming Git LFS...
git lfs install
if errorlevel 1 goto :fail
git lfs track "*.onnx"
git lfs track "*.npy"
git lfs track "*.bin"

echo.
echo [3/5] Adding all files...
git add .
if errorlevel 1 goto :fail

echo.
echo Large files handled by Git LFS:
git lfs ls-files
echo.

echo [4/5] Creating the commit...
git diff --cached --quiet
if not errorlevel 1 (
  echo Nothing new is staged.
  git rev-parse --verify HEAD >nul 2>&1
  if errorlevel 1 (
    echo ERROR: There is still no commit to push.
    goto :fail
  )
) else (
  git commit -m "Daggerfall Voice Engine v1.0.2"
  if errorlevel 1 goto :fail
)

echo.
echo [5/5] Uploading to GitHub...
git branch -M main

git remote get-url origin >nul 2>&1
if errorlevel 1 (
  git remote add origin "%REPO_URL%"
) else (
  git remote set-url origin "%REPO_URL%"
)

git push -u origin main
if errorlevel 1 goto :pushfail

echo.
echo ============================================================
echo  SUCCESS
echo ============================================================
echo.
echo Uploaded to:
echo %REPO_URL%
echo.
pause
exit /b 0

:pushfail
echo.
echo ============================================================
echo  PUSH FAILED
echo ============================================================
echo.
echo Do NOT force-push.
echo Copy the error shown above and send it to ChatGPT.
echo.
pause
exit /b 1

:fail
echo.
echo ============================================================
echo  SOMETHING FAILED
echo ============================================================
echo.
echo Copy the error shown above and send it to ChatGPT.
echo.
pause
exit /b 1
