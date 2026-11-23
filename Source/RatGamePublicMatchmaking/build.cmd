@echo off
setlocal
set "DOTNET_CLI_HOME=%~dp0.cache\dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
set "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "MSBuildEnableWorkloadResolver=false"
set "NUGET_PACKAGES=%~dp0.cache\packages"
set "NUGET_HTTP_CACHE_PATH=%~dp0.cache\http"
set "RATGAME_ROOT=%~dp0..\.."
if not "%~1"=="" set "RATGAME_ROOT=%~1"
set "RATGAME_BEP_CORE=%RATGAME_ROOT%\BepInEx\core"
if not "%~2"=="" set "RATGAME_BEP_CORE=%~2"
dotnet build "%~dp0src\RatGamePublicMatchmaking.csproj" --configuration Release --nologo -p:RatGameDir="%RATGAME_ROOT%" -p:BepInExCoreDir="%RATGAME_BEP_CORE%"
if errorlevel 1 exit /b 1
dotnet run --project "%~dp0tests\SafetyTests.csproj" --configuration Release --no-launch-profile
if errorlevel 1 exit /b 1
dotnet run --project "%~dp0validation\Validation.csproj" --configuration Release --no-launch-profile -p:CecilPath="%RATGAME_BEP_CORE%\Mono.Cecil.dll" -- "%~dp0." "%RATGAME_ROOT%\Rat Game_Data\Managed" "%RATGAME_BEP_CORE%"
exit /b %errorlevel%
