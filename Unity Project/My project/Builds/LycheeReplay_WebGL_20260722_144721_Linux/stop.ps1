$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidPath = Join-Path $root '.lychee_web_server.pid'
if (-not (Test-Path $pidPath)) { Write-Host 'No Lychee WebGL server pid file found.'; exit 0 }
$pidValue = Get-Content $pidPath -ErrorAction SilentlyContinue
$serverPid = 0
[int]::TryParse($pidValue, [ref]$serverPid) | Out-Null
if ($serverPid -gt 0) { Stop-Process -Id $serverPid -ErrorAction SilentlyContinue }
Remove-Item $pidPath -ErrorAction SilentlyContinue
Write-Host 'Stopped Lychee WebGL server.'
