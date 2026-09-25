@echo off
set TARGET=%APPDATA%\Autodesk\Revit\Addins\2025
if not exist "%TARGET%\FireAlarmAddin" mkdir "%TARGET%\FireAlarmAddin"
copy /Y "%~dp0FireAlarmAddin\FireAlarmAddin.dll" "%TARGET%\FireAlarmAddin\"
copy /Y "%~dp0FireAlarmAddin.addin" "%TARGET%\"
echo Terpasang di %TARGET%. Silakan buka ulang Revit 2025.
pause
