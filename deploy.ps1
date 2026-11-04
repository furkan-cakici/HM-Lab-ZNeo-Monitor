$desktop = [System.Environment]::GetFolderPath('Desktop')
$srcDir = Join-Path $PSScriptRoot "HMHubScreenMonitor\bin\Release\net10.0-windows"
$destDesk = Join-Path $desktop "HM_Lab_Monitor"
$destProg = "$env:ProgramData\HMHubMonitor"

if (-not (Test-Path $srcDir)) {
    Write-Host "Building Release binary..."
    dotnet build (Join-Path $PSScriptRoot "HMHubScreenMonitor\HMHubScreenMonitor.csproj") -c Release
}

if (-not (Test-Path $destDesk)) { New-Item -ItemType Directory -Path $destDesk -Force | Out-Null }
if (-not (Test-Path $destProg)) { New-Item -ItemType Directory -Path $destProg -Force | Out-Null }

taskkill /F /IM ZNeoScreenService.exe 2>$null
Start-Sleep -Milliseconds 600

Get-ChildItem $srcDir -File | ForEach-Object {
    Copy-Item $_.FullName $destDesk -Force
    Copy-Item $_.FullName $destProg -Force
}

$scriptsDir = Join-Path $PSScriptRoot "scripts"
if (Test-Path $scriptsDir) {
    Get-ChildItem $scriptsDir -File | ForEach-Object {
        Copy-Item $_.FullName $destDesk -Force
        Copy-Item $_.FullName $destProg -Force
    }
}

Get-ChildItem $destDesk | Unblock-File
Get-ChildItem $destProg | Unblock-File

Start-Process "cmd.exe" -ArgumentList "/c start `"`" `"$destProg\ZNeoScreenService.exe`"" -WindowStyle Hidden
Start-Sleep -Milliseconds 800

$proc = Get-Process -Name ZNeoScreenService -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Deploy complete. ZNeoScreenService running (PID: $($proc.Id))."
} else {
    Write-Host "Deploy complete. Note: Service did not start automatically."
}
