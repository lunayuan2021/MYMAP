$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidPath = Join-Path $root '.lychee_web_server.pid'
$url = 'http://127.0.0.1:9091/litchi_delivery_replay/'
if (Test-Path $pidPath) {
  $oldPid = [int](Get-Content $pidPath -ErrorAction SilentlyContinue)
  if ($oldPid -gt 0 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
    Write-Host "Server already running on port 9091 (PID $oldPid)."
    Start-Process $url
    exit 0
  }
}
$server = Join-Path $root 'start_local_server.ps1'
$p = Start-Process powershell -WindowStyle Minimized -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$server)
Set-Content -Path $pidPath -Value $p.Id
Write-Host "Started Lychee WebGL server on http://127.0.0.1:9091/litchi_delivery_replay/ (PID $($p.Id))."
# Browser is opened once by start_local_server.ps1 after the listener is ready; do not open again here.
