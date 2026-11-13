@echo off
cd /d "%~dp0"
echo Building SubnauticaMP... > build.log
dotnet build SubnauticaMP\SubnauticaMP.csproj >> build.log 2>&1
set RC=%ERRORLEVEL%
echo BUILD EXIT CODE %RC% >> build.log
if not "%RC%"=="0" goto done
set PLUG=C:\Program Files (x86)\Steam\steamapps\common\Subnautica\BepInEx\plugins\SubnauticaMP
if not exist "%PLUG%" mkdir "%PLUG%" >> build.log 2>&1
copy /Y "SubnauticaMP\bin\Debug\net472\SubnauticaMP.dll" "%PLUG%\" >> build.log 2>&1
copy /Y "SubnauticaMP\bin\Debug\net472\MPCore.dll" "%PLUG%\" >> build.log 2>&1
echo COPY EXIT CODE %ERRORLEVEL% >> build.log
:done
echo DONE >> build.log
type build.log
timeout /t 5 >nul
