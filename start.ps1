# Starts the .NET API (http://localhost:5080) and the React UI (http://localhost:5173) in two windows.
$root = $PSScriptRoot

if (-not (Test-Path "$root\frontend\node_modules")) {
    Push-Location "$root\frontend"; npm install; Pop-Location
}

Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\backend'; dotnet run --project Rag.Api"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\frontend'; npm run dev"

Start-Sleep -Seconds 8
Start-Process 'http://localhost:5173'
