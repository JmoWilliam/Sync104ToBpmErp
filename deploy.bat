@echo off
chcp 65001 >nul
echo ========================================
echo Sync104ToBpmErp 部署打包腳本
echo ========================================
echo.

set PROJECT_NAME=Sync104ToBpmErp
set PUBLISH_DIR=.\publish
set DEPLOY_DIR=.\deploy
set PACKAGE_NAME=Sync104ToBpmErp_部署包_%date:~0,4%%date:~5,2%%date:~8,2%

echo [1/5] 清理舊的發布檔案...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%DEPLOY_DIR%" rmdir /s /q "%DEPLOY_DIR%"
echo 完成
echo.

echo [2/5] 發布程式...
dotnet publish -c Release -o "%PUBLISH_DIR%" --self-contained false
echo.

if errorlevel 1 (
    echo [錯誤] 發布失敗!
    pause
    exit /b 1
)

echo [3/5] 建立部署目錄結構...
mkdir "%DEPLOY_DIR%\%PACKAGE_NAME%"
mkdir "%DEPLOY_DIR%\%PACKAGE_NAME%\Logs"

echo [4/5] 複製必要檔案...

:: 複製執行檔
copy "%PUBLISH_DIR%\%PROJECT_NAME%.exe" "%DEPLOY_DIR%\%PACKAGE_NAME%\" >nul
copy "%PUBLISH_DIR%\%PROJECT_NAME%.dll" "%DEPLOY_DIR%\%PACKAGE_NAME%\" >nul
copy "%PUBLISH_DIR%\%PROJECT_NAME%.runtimeconfig.json" "%DEPLOY_DIR%\%PACKAGE_NAME%\" >nul
copy "%PUBLISH_DIR%\*.dll" "%DEPLOY_DIR%\%PACKAGE_NAME%\" >nul

:: 複製設定檔 (範本)
copy "appsettings.json" "%DEPLOY_DIR%\%PACKAGE_NAME%\appsettings.json" >nul

:: 複製 SQL 檔案
mkdir "%DEPLOY_DIR%\%PACKAGE_NAME%\SQL" >nul
copy "SQL\*.sql" "%DEPLOY_DIR%\%PACKAGE_NAME%\SQL\" >nul

:: 複製說明文件
copy "README.md" "%DEPLOY_DIR%\%PACKAGE_NAME%\使用說明.md" >nul
copy "部署說明.md" "%DEPLOY_DIR%\%PACKAGE_NAME%\部署說明.md" >nul

echo 完成
echo.

echo [5/5] 建立壓縮檔...
cd "%DEPLOY_DIR%"
powershell -Command "Compress-Archive -Path '%PACKAGE_NAME%' -DestinationPath '%PACKAGE_NAME%.zip' -Force"
cd ..
echo.

echo ========================================
echo 部署包建立完成!
echo 位置: %DEPLOY_DIR%\%PACKAGE_NAME%.zip
echo ========================================
echo.
pause
