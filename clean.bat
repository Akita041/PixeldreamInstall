@echo off
setlocal
set "ROOT=%~dp0"

if exist "%ROOT%build\." rd /s /q "%ROOT%build"
if exist "%ROOT%dist\." rd /s /q "%ROOT%dist"
if exist "%ROOT%code\__pycache__\." rd /s /q "%ROOT%code\__pycache__"
if exist "%ROOT%__pycache__\." rd /s /q "%ROOT%__pycache__"
if exist "%ROOT%.pytest_cache\." rd /s /q "%ROOT%.pytest_cache"
if exist "%ROOT%.mypy_cache\." rd /s /q "%ROOT%.mypy_cache"
if exist "%ROOT%.ruff_cache\." rd /s /q "%ROOT%.ruff_cache"
if exist "%ROOT%htmlcov\." rd /s /q "%ROOT%htmlcov"

del /s /q "%ROOT%*.pyc" "%ROOT%*.pyo" >nul 2>nul
del /q "%ROOT%.coverage" >nul 2>nul
echo Build artifacts and caches cleaned.
exit /b 0
