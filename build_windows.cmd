@echo off
cd /d "%~dp0DynamicIsland"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=1.0.0-layout.1 -o publish
pause
