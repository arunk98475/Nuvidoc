@echo off

set CHROME1=C:\Program Files\Google\Chrome\Application\chrome.exe
set CHROME2=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe
set URL=http://52.35.15.203

if exist "%CHROME1%" (
    start "" "%CHROME1%" --unsafely-treat-insecure-origin-as-secure=%URL% %URL%
    exit /b
)

if exist "%CHROME2%" (
    start "" "%CHROME2%" --unsafely-treat-insecure-origin-as-secure=%URL% %URL%
    exit /b
)

echo Chrome not found.
pause