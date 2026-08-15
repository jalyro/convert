@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."
for %%d in (out stage pkgsrc) do (
    if exist "%%d" ( rmdir /s /q "%%d" && echo removed %%d )
)
echo Done.
