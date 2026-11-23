@echo off
chcp 65001 > nul
title StockLab KIS API Profile Setup (DPAPI Vault)
dotnet run --project "%~dp0Client\Client.csproj" -- --kis-setup
pause

