$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$port = 9091
$url = "http://127.0.0.1:$port/litchi_delivery_replay/"
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$mime = @{
  '.html'='text/html; charset=utf-8'; '.js'='application/javascript'; '.css'='text/css';
  '.wasm'='application/wasm'; '.unityweb'='application/octet-stream'; '.data'='application/octet-stream';
  '.json'='application/json'; '.txt'='text/plain; charset=utf-8'; '.png'='image/png'; '.jpg'='image/jpeg';
  '.jpeg'='image/jpeg'; '.ico'='image/x-icon'; '.mp4'='video/mp4'; '.md'='text/plain; charset=utf-8'
}
$gzipExts = @('.txt', '.json')
$logFile = Join-Path $root '.lychee_web_server_requests.log'
function Log-Line($text) {
  try { Add-Content -Path $logFile -Value ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $text) -Encoding UTF8 } catch {}
}
function Send-Status($ctx, $code, $text) {
  try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $ctx.Response.StatusCode = $code
    $ctx.Response.ContentType = 'text/plain; charset=utf-8'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
  } catch {}
}
function Get-ContentType($full) {
  if ($full.EndsWith('.wasm.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/wasm' }
  if ($full.EndsWith('.framework.js.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/javascript' }
  if ($full.EndsWith('.data.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/octet-stream' }
  $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
  if ($mime.ContainsKey($ext)) { return $mime[$ext] }
  return 'application/octet-stream'
}
try { $listener.Start() } catch {
  try {
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    $url = "http://localhost:$port/litchi_delivery_replay/"
    Write-Host "127.0.0.1 binding denied (needs elevation); fell back to $url"
    Log-Line "START-FALLBACK localhost binding in use"
  } catch {
    Write-Host "Failed to start http://127.0.0.1:$port/ and http://localhost:$port/"
    Write-Host $_.Exception.Message
    Read-Host 'Press Enter to close'
    exit 1
  }
}
Write-Host "Serving $root at $url"
Write-Host "Opening $url"
Log-Line "START root=$root port=$port"
try { Start-Process $url } catch { Write-Host "Could not open a browser automatically. Open $url manually." }
try {
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $rawUrl = $ctx.Request.RawUrl
    $sw = $null
    try {
      $requestPath = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)
      $prefix = '/litchi_delivery_replay'
      if ($requestPath -eq $prefix -or $requestPath -eq ($prefix + '/')) { $requestPath = '/' }
      elseif ($requestPath.StartsWith($prefix + '/', [StringComparison]::OrdinalIgnoreCase)) { $requestPath = $requestPath.Substring($prefix.Length) }
      $rootFull = [IO.Path]::GetFullPath($root)
      if ([string]::IsNullOrWhiteSpace($requestPath) -or $requestPath -eq '/') {
        $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, 'index.html'))
      } elseif ($requestPath.StartsWith('/data/', [StringComparison]::OrdinalIgnoreCase)) {
        $full = [IO.Path]::GetFullPath($requestPath)
      } else {
        $relative = $requestPath.TrimStart('/') -replace '/', [IO.Path]::DirectorySeparatorChar
        $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
        if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { Log-Line "403 $rawUrl"; Send-Status $ctx 403 'Forbidden'; continue }
      }
      if (-not [IO.File]::Exists($full)) { Log-Line "404 $rawUrl -> $full"; Send-Status $ctx 404 'Not Found'; continue }
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $ctx.Response.StatusCode = 200
      $ctx.Response.ContentType = Get-ContentType $full
      $ctx.Response.Headers['Cache-Control'] = 'no-store'
      $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
      $acceptEnc = [string]$ctx.Request.Headers['Accept-Encoding']
      if ($full.EndsWith('.unityweb', [StringComparison]::OrdinalIgnoreCase)) {
        $stream = [IO.File]::OpenRead($full)
        $sent = 0
        try {
          $ctx.Response.Headers['Content-Encoding'] = 'gzip'
          $ctx.Response.ContentLength64 = $stream.Length
          $buffer = New-Object byte[] 65536
          while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $ctx.Response.OutputStream.Write($buffer, 0, $read); $sent += $read }
        } finally {
          $stream.Dispose()
          try { $ctx.Response.Close() } catch {}
        }
        Log-Line ("200 $rawUrl (" + $sent + " bytes, " + $sw.ElapsedMilliseconds + " ms)")
      } elseif (($gzipExts -contains $ext) -and $acceptEnc.Contains('gzip')) {
        $raw = [IO.File]::ReadAllBytes($full)
        $ms = New-Object IO.MemoryStream
        $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress, $true)
        $gz.Write($raw, 0, $raw.Length)
        $gz.Dispose()
        $bytes = $ms.ToArray()
        $ms.Dispose()
        $ctx.Response.Headers['Content-Encoding'] = 'gzip'
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        try { $ctx.Response.Close() } catch {}
        Log-Line ("200 $rawUrl (" + $raw.Length + " -> " + $bytes.Length + " bytes gzip, " + $sw.ElapsedMilliseconds + " ms)")
      } else {
        $stream = [IO.File]::OpenRead($full)
        $sent = 0
        try {
          $ctx.Response.ContentLength64 = $stream.Length
          $buffer = New-Object byte[] 65536
          while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $ctx.Response.OutputStream.Write($buffer, 0, $read); $sent += $read }
        } finally {
          $stream.Dispose()
          try { $ctx.Response.Close() } catch {}
        }
        Log-Line ("200 $rawUrl (" + $sent + " bytes, " + $sw.ElapsedMilliseconds + " ms)")
      }
    } catch {
      $elapsed = -1
      if ($sw) { $elapsed = $sw.ElapsedMilliseconds }
      Log-Line ("ERR $rawUrl :: " + $_.Exception.Message + " (after " + $elapsed + " ms)")
      Send-Status $ctx 500 'Server Error'
    }
  }
} finally { $listener.Stop(); $listener.Close(); Log-Line 'STOP' }
