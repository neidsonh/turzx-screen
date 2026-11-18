# Deixa o LibreHardwareMonitor totalmente invisivel (sem janela, sem icone na bandeja).
# Ele passa a rodar como SYSTEM na "sessao 0" (sem area de trabalho), iniciando junto com o Windows.
# Sensores e o servidor web local (127.0.0.1:8085) continuam funcionando; os dados podem ser vistos em http://127.0.0.1:8085
#
# Rodar em um PowerShell aberto como ADMINISTRADOR:
#   powershell -ExecutionPolicy Bypass -File "<pasta do projeto>\tools\lhm-ocultar.ps1"
# Para desfazer:  ... -File lhm-ocultar.ps1 -Desfazer   (volta a abrir na sua sessao, com icone na bandeja)
param([switch]$Desfazer)

$exe = "$env:LOCALAPPDATA\Programs\LibreHardwareMonitor\LibreHardwareMonitor.exe"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
  throw "Abra o PowerShell como Administrador."
}
if (-not (Test-Path $exe)) { throw "LibreHardwareMonitor nao encontrado em $exe" }

Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue | % { $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(5000)) { $_.Kill() } }
Start-Sleep 1

$action   = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable

if ($Desfazer) {
  $user = "$env:USERDOMAIN\$env:USERNAME"
  $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
  $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
} else {
  $trigger   = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
}
Register-ScheduledTask -TaskPath '\LibreHardwareMonitor\' -TaskName 'Startup' -Action $action -Trigger $trigger `
  -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskPath '\LibreHardwareMonitor\' -TaskName 'Startup'
Start-Sleep 10

$p = Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue
if ($p) { "LibreHardwareMonitor rodando (sessao $($p.SessionId))" + $(if ($p.SessionId -eq 0) { " - invisivel" } else { "" }) } else { "ATENCAO: nao iniciou" }
try { Invoke-RestMethod 'http://127.0.0.1:8085/data.json' -TimeoutSec 5 | Out-Null; "Servidor de sensores OK - o TURZX Screen ja pode ler a temperatura." }
catch { "Servidor de sensores ainda nao respondeu: $($_.Exception.Message)" }
