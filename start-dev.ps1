# รัน backend และ frontend พร้อมกันสำหรับการพัฒนา
# ใช้งาน:  .\start-dev.ps1
# ปิด:     ปิดหน้าต่าง PowerShell ทั้งสองบานที่เปิดขึ้นมา

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$apiPath = Join-Path $root 'Backend\MgtAiAuthen.Api'
$webPath = Join-Path $root 'Frontend'

if (-not (Test-Path (Join-Path $webPath 'node_modules'))) {
    Write-Host 'ยังไม่มี node_modules — กำลังติดตั้ง dependencies ของ frontend...' -ForegroundColor Yellow
    Push-Location $webPath
    npm install
    Pop-Location
}

Write-Host 'เปิด backend  → http://localhost:5080  (Swagger: /swagger)' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
    '-NoExit', '-Command',
    "Set-Location '$apiPath'; `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --urls http://localhost:5080"
)

Write-Host 'เปิด frontend → http://localhost:5173' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
    '-NoExit', '-Command',
    "Set-Location '$webPath'; npm run dev"
)

Write-Host ''
Write-Host 'ผู้ใช้ทดสอบ:' -ForegroundColor Green
Write-Host '  admin01 / Admin@2026    (Admin   — แชท + ดู log ทุกคน + จัดการระบบ)'
Write-Host '  somchai / Somchai@2026  (User    — แชทได้เท่านั้น)'
Write-Host '  sunee   / Sunee@2026    (Auditor — ดู log ได้ ไม่แก้ไขระบบ)'
