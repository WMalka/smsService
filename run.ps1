# Fallback without Docker (Windows): needs the .NET 8 SDK and Node 20.
# Starts the API and the Vite dev server; Ctrl+C stops both.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path 'web/node_modules')) { npm.cmd --prefix web install }

$api = Start-Process dotnet -ArgumentList 'run', '--project', 'src/Sms.Api' -NoNewWindow -PassThru
$web = Start-Process npm.cmd -ArgumentList 'run', 'dev' -WorkingDirectory 'web' -NoNewWindow -PassThru

Write-Host ''
Write-Host 'API: http://localhost:5080   (health: /health)'
Write-Host 'UI:  http://localhost:5173'
Write-Host 'Press Ctrl+C to stop.'

try {
    while (-not $api.HasExited -and -not $web.HasExited) { Start-Sleep -Seconds 1 }
}
finally {
    # /T kills the child processes too (dotnet run -> Sms.Api, npm -> node).
    foreach ($p in $api, $web) {
        if (-not $p.HasExited) { taskkill /PID $p.Id /T /F | Out-Null }
    }
}
