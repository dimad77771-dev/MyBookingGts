@echo off
setlocal
cd /d "%~dp0"
dotnet run --project MyBookingGts.csproj
set EXIT_CODE=%ERRORLEVEL%
echo.
echo My Booking GTS finished with exit code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
