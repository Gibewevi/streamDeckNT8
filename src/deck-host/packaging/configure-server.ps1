# Shared by the Inno installer and the manual installer. No process or trading state is touched.
param(
  [Parameter(Mandatory = $true)][string]$BitlearnUrl,
  [string]$StateDir = "$env:APPDATA\StreamDeckTrader",
  [string]$LocalStateDir = "$env:LOCALAPPDATA\StreamDeckTrader",
  [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'

function Normalize-Server([string]$Value) {
  $parsed = $null
  if (-not [uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$parsed)) { return $null }
  if ($parsed.UserInfo) { return $null }
  $origin = $parsed.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
  if ($origin -in @('https://bitlearn.fr', 'https://dev.bitlearn.fr')) { return $origin }
  if ($parsed.Scheme -eq 'http' -and $parsed.Host -in @('localhost', '127.0.0.1', '[::1]', '::1')) { return $origin }
  return $null
}

$target = Normalize-Server $BitlearnUrl
if (-not $target) { throw 'Serveur interdit : production, dev ou HTTP local uniquement.' }
if ($ValidateOnly) { Write-Output $target; return }
$config = Join-Path $StateDir 'bitlearn.json'
$previous = 'https://bitlearn.fr'
if (Test-Path -LiteralPath $config) {
  try { $old = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json; $normalized = Normalize-Server $old.url; if ($normalized) { $previous = $normalized } } catch { }
}

New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
if ($previous -ne $target) {
  # Archive credentials and seal state before removing them. Spools/cursors/layout/safety remain
  # byte-for-byte intact, including unsent lines signed by the previous environment.
  $archive = Join-Path $StateDir ('environment-archives\' + (Get-Date -Format 'yyyyMMddTHHmmssfff') + '-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $archive -Force | Out-Null
  $files = @(
    @{Path=(Join-Path $StateDir 'device.json');Name='device-roaming.json'},
    @{Path=(Join-Path $LocalStateDir 'device.json');Name='device-local.json'},
    @{Path=(Join-Path $StateDir 'journal\journal.key');Name='journal.key'},
    @{Path=(Join-Path $StateDir 'journal\seal-state.json');Name='seal-state.json'},
    @{Path=(Join-Path $StateDir 'journal\seal-state-exec.json');Name='seal-state-exec.json'}
  )
  foreach ($file in $files) {
    if (Test-Path -LiteralPath $file.Path) { Copy-Item -LiteralPath $file.Path -Destination (Join-Path $archive $file.Name) -Force }
  }
  # Delete only the explicitly named copied files, after every backup has succeeded.
  foreach ($file in $files) {
    if (Test-Path -LiteralPath $file.Path) { Remove-Item -LiteralPath $file.Path -Force }
  }
  Write-Output 'Changement de serveur : nouvel appairage requis ; journal et protections conserves.'
}
$temporary = "$config.$([guid]::NewGuid().ToString('N')).tmp"
[IO.File]::WriteAllText($temporary, ((@{url=$target} | ConvertTo-Json -Compress) + "`r`n"), (New-Object Text.UTF8Encoding($false)))
# Move-Item -Force performs a file replacement, not a recursive operation.
Move-Item -LiteralPath $temporary -Destination $config -Force
