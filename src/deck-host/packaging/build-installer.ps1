# Construit l'installateur Bitlearn TradeDeck.
#
#   powershell -ExecutionPolicy Bypass -File packaging\build-installer.ps1
#
# Trois étapes : construire les sources, assembler une charge utile propre, puis la compresser
# en un unique .exe.
#
# La charge utile est assemblée à part plutôt que prise dans l'arbre de développement, parce que
# `node_modules` y contient TypeScript et les typages — 26 Mo qui n'ont rien à faire chez un
# utilisateur, et qui allongent le téléchargement pour rien.

param(
  [string]$Version,
  # Chemin d'un certificat .pfx. Sans lui, l'installateur n'est PAS signé : voir l'avertissement
  # en fin de script.
  [string]$SignPfx,
  [string]$SignPassword,
  [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$deckHost = Split-Path $PSScriptRoot -Parent
$repo = Split-Path (Split-Path $deckHost -Parent) -Parent
$build = Join-Path $repo 'build'
$payload = Join-Path $build 'payload'

function Etape($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

if (-not $Version) {
  $Version = (Get-Content (Join-Path $deckHost 'package.json') -Raw | ConvertFrom-Json).version
}

# --- 1. Construction ---------------------------------------------------------------
if (-not $SkipBuild) {
  Etape "Construction de l'hote et du bridge"
  Push-Location $deckHost
  try {
    npm run build:all
    if ($LASTEXITCODE -ne 0) { throw "npm run build:all a echoue" }
  } finally { Pop-Location }
}

foreach ($requis in @('dist\host.js', 'bridge\StreamDeckBridge.exe', 'ui\index.html')) {
  if (-not (Test-Path (Join-Path $deckHost $requis))) { throw "Manquant apres construction : $requis" }
}

# --- 2. Charge utile ---------------------------------------------------------------
Etape "Assemblage de la charge utile"
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

foreach ($d in @('dist', 'ui', 'bridge')) {
  Copy-Item (Join-Path $deckHost $d) (Join-Path $payload $d) -Recurse -Force
}
Copy-Item (Join-Path $deckHost 'package.json') $payload -Force
Copy-Item (Join-Path $PSScriptRoot 'TradeDeck.vbs') $payload -Force
# Lanceur silencieux appelé par la tâche planifiée : sans lui, Node ouvre une fenêtre console.
Copy-Item (Join-Path $PSScriptRoot 'run-host.vbs') $payload -Force

# Dépendances de production seulement. `npm ci --omit=dev` dans un dossier isolé plutôt qu'un
# élagage de l'arbre de développement : élaguer sur place casserait la compilation locale, et il
# faudrait réinstaller après chaque construction d'installateur.
Etape "Installation des dependances de production"
Copy-Item (Join-Path $deckHost 'package.json') (Join-Path $payload 'package.json') -Force
Copy-Item (Join-Path $deckHost 'package-lock.json') (Join-Path $payload 'package-lock.json') -Force
Push-Location $payload
try {
  npm ci --omit=dev --ignore-scripts=false 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "npm ci a echoue" }
} finally { Pop-Location }
Remove-Item (Join-Path $payload 'package-lock.json') -Force -ErrorAction SilentlyContinue

# node.exe est embarqué : on ne peut pas supposer Node installé chez un trader.
Etape "Ajout du moteur Node"
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw "node.exe introuvable dans le PATH" }
Copy-Item $node (Join-Path $payload 'node.exe') -Force

$taille = [math]::Round((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  charge utile : $taille Mo avant compression"

# --- 3. Compilation de l'installateur ----------------------------------------------
Etape "Compilation de l'installateur"
# Les accolades sont indispensables : `$env:ProgramFiles(x86)` s'interprete comme `$env:ProgramFiles`
# suivi d'un `(x86)` litteral, et le chemin ne correspond alors jamais.
# winget installe par utilisateur dans %LOCALAPPDATA%\Programs — a chercher en premier.
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 introuvable. winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DPayload=$payload" "/DOutDir=$build" (Join-Path $PSScriptRoot 'TradeDeck.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC a echoue" }

$exe = Get-ChildItem $build -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$mo = [math]::Round($exe.Length / 1MB, 1)
Write-Host "`nInstallateur : $($exe.FullName) ($mo Mo)" -ForegroundColor Green

# --- 4. Signature ------------------------------------------------------------------
if ($SignPfx) {
  Etape "Signature"
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
  if (-not $signtool) { throw "signtool.exe introuvable (Windows SDK requis)" }
  & $signtool.FullName sign /f $SignPfx /p $SignPassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $exe.FullName
  if ($LASTEXITCODE -ne 0) { throw "La signature a echoue" }
  Write-Host "Installateur signe." -ForegroundColor Green
} else {
  Write-Warning @"
Installateur NON SIGNE.

Windows SmartScreen affichera « Editeur inconnu » et cachera le bouton d'execution derriere
« Informations complementaires ». Sur un produit payant, c'est un frein de conversion reel.

Pour signer : -SignPfx <chemin.pfx> -SignPassword <motdepasse>
Un certificat de signature de code OV coute 200-400 EUR/an. La reputation SmartScreen se
construit ensuite sur quelques centaines de telechargements ; un certificat EV l'accorde
immediatement, pour environ le double.
"@
}
