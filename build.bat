@echo off
chcp 65001 >nul
rem Сборка надстройки. Запускать двойным щелчком из папки мода (Mods\RimWorldBot).
where dotnet >nul 2>nul || (echo Не найден .NET SDK. Установите: https://dotnet.microsoft.com/download ^(SDK 8^) & pause & exit /b 1)
set "RW=C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
if not exist "%RW%\RimWorldWin64_Data\Managed\Assembly-CSharp.dll" (
  set /p RW=Путь к папке RimWorld: 
)
dotnet build RimWorldBot.TradeEmpire.csproj -c Release -p:RimWorldDir="%RW%"
if errorlevel 1 (echo. & echo ОШИБКИ СБОРКИ — скопируйте текст выше и пришлите мне. & pause & exit /b 1)
if exist Assemblies\RimWorldBot.TradeEmpire.dll (echo. & echo ГОТОВО: Assemblies\RimWorldBot.TradeEmpire.dll) else (echo DLL не появился — проверьте OutputPath)
pause
