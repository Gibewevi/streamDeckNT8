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
  # Serveur Bitlearn que le paquet vise. Il finit à trois endroits qui doivent s'accorder : le
  # fichier écrit par l'installateur, le repli du lanceur, et le nom du .exe produit. Les laisser
  # se régler séparément reviendrait à pouvoir publier un paquet de développement qui ouvre
  # la production.
  [string]$BitlearnUrl = 'https://bitlearn.fr',
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
$ntPayload = Join-Path $build 'ninjatrader'

function Etape($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

if (-not $Version) {
  $Version = (Get-Content (Join-Path $deckHost 'package.json') -Raw | ConvertFrom-Json).version
}

# Ramenée à une origine : `https://dev.bitlearn.fr/tradedeck/` et `https://dev.bitlearn.fr`
# doivent produire le même paquet. Un chemin résiduel donnerait
# `.../tradedeck/tradedeck/configuration`, soit un 404 dont la cause serait invisible côté poste.
$PRODUCTION = 'https://bitlearn.fr'
try { $uri = [uri]$BitlearnUrl } catch { throw "BitlearnUrl illisible : $BitlearnUrl" }
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http', 'https')) {
  throw "BitlearnUrl doit être une adresse http(s) absolue : $BitlearnUrl"
}
$BitlearnUrl = '{0}://{1}' -f $uri.Scheme, $uri.Authority

# Un paquet qui ne vise pas la production porte sa cible dans son nom. Deux .exe de même
# version pointant deux serveurs sont autrement indiscernables une fois téléchargés, et se
# tromper des deux mène à un 404 muet. `-dev` pour dev.bitlearn.fr, `-localhost` en local.
$suffixe = ''
if ($BitlearnUrl -ne $PRODUCTION) {
  $etiquette = ($uri.Host -split '\.')[0].ToLowerInvariant() -replace '[^a-z0-9]', ''
  if (-not $etiquette) { $etiquette = 'autre' }
  $suffixe = "-$etiquette"
}
Write-Host "Serveur Bitlearn visé : $BitlearnUrl" -ForegroundColor Yellow

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

# Le bridge doit être AUTONOME. Publié dépendant du framework, il exige un runtime .NET 8 que
# Windows n'embarque pas : l'exe se termine aussitôt, et comme le superviseur le lance en
# `windowsHide` avec les sorties ignorées, personne ne voit le message. Côté client cela donne
# deux voyants rouges sans explication — bridge, puis NinjaTrader dont l'état transite par lui.
# Vécu le 19/08/2026 sur la première installation payante.
#
# `hostfxr.dll` est le marqueur : il n'existe que dans une publication autonome. Le contrôle est
# ici parce qu'un `dotnet publish` lancé à la main, sans `--self-contained`, réécrit le
# dossier sans rien signaler.
$marqueur = Join-Path $deckHost 'bridge\hostfxr.dll'
if (-not (Test-Path $marqueur)) {
  throw "Le bridge n'est pas autonome : hostfxr.dll absent de bridge/. Reconstruire avec npm run build:bridge, qui publie en --self-contained."
}

# --- 2. Charge utile ---------------------------------------------------------------
Etape "Assemblage de la charge utile"
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

foreach ($d in @('dist', 'ui', 'bridge')) {
  Copy-Item (Join-Path $deckHost $d) (Join-Path $payload $d) -Recurse -Force
}
Copy-Item (Join-Path $deckHost 'package.json') $payload -Force
# Le lanceur part avec le repli de SA cible, pas avec la production en dur : un paquet de
# développement dont le fichier de configuration aurait disparu ouvrirait sinon bitlearn.fr.
# L'ancre est vérifiée avant substitution — une substitution muette produirait un paquet
# annoncé comme dev et repliant sur la production, l'exacte panne qu'on corrige ici.
#
# Lecture et écriture en UTF-8 SANS BOM, par .NET : `Set-Content -Encoding UTF8` en PowerShell
# 5.1 pose une BOM, et wscript.exe la lit comme trois caractères parasites en tête de script.
$ancre = 'Const REPLI = "https://bitlearn.fr"'
$lanceur = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TradeDeck.vbs'), [System.Text.Encoding]::UTF8)
if (-not $lanceur.Contains($ancre)) { throw "Repli introuvable dans TradeDeck.vbs : la substitution du serveur ne s'applique plus" }
$lanceur = $lanceur.Replace($ancre, 'Const REPLI = "' + $BitlearnUrl + '"')
[System.IO.File]::WriteAllText((Join-Path $payload 'TradeDeck.vbs'), $lanceur, (New-Object System.Text.UTF8Encoding($false)))
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

# --- 2 bis. Sources NinjaScript ------------------------------------------------------
# Assemblées à part : l'installateur les dépose dans `Documents\NinjaTrader 8\bin\Custom`,
# pas dans le dossier de l'hôte. Les mêler à la charge utile les copierait aux deux endroits.
#
# La séparation des deux dossiers n'est pas cosmétique : `TrendEngine` référence
# `TdSwingEngine`, qui doit exister en UNE seule copie. Deux exemplaires lèvent un CS0101, et une
# compilation NinjaScript est tout ou rien — l'échec emporterait les indicateurs du trader.
Etape "Assemblage des sources NinjaScript"
if (Test-Path $ntPayload) { Remove-Item $ntPayload -Recurse -Force }
New-Item -ItemType Directory -Path "$ntPayload/AddOns/StreamDeck" -Force | Out-Null
New-Item -ItemType Directory -Path "$ntPayload/Indicators" -Force | Out-Null

Get-ChildItem (Join-Path $repo 'src/NinjaTrader.AddOn.StreamDeck') -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*\bin\*' } |
  ForEach-Object { Copy-Item $_.FullName "$ntPayload/AddOns/StreamDeck" -Force }
Copy-Item (Join-Path $repo 'src/NinjaTrader.Scripts/Indicators/TdSwingEngine.cs') "$ntPayload/Indicators" -Force

$sources = @(Get-ChildItem "$ntPayload/AddOns/StreamDeck" -Filter *.cs)
if ($sources.Count -lt 10) { throw "Sources de l'add-on introuvables : $($sources.Count) fichier(s) seulement" }
# Un `AssemblyInfo.cs` ou un `AssemblyAttributes.cs` venu de `obj\` ne gênerait pas la
# construction : il exploserait chez le trader, en attributs d'assembly dupliqués, et une
# compilation NinjaScript est tout ou rien. C'est arrivé à la première écriture de ce
# script, un `[\/]` en regex .NET ne contenant que la barre oblique.
$generes = @($sources | Where-Object { $_.Name -match 'AssemblyInfo|AssemblyAttributes' })
if ($generes) { throw "Artefacts generes dans les sources de l'add-on : $($generes.Name -join ', ')" }
if (Test-Path "$ntPayload/AddOns/StreamDeck/TdSwingEngine.cs") {
  throw "TdSwingEngine.cs se trouve dans AddOns/StreamDeck : ce doublon leverait un CS0101 chez le trader."
}
if (-not (Test-Path "$ntPayload/Indicators/TdSwingEngine.cs")) { throw "TdSwingEngine.cs manquant dans Indicators/" }
# La liste part dans l'installateur : c'est elle qui lui permet de retirer, APRES la copie, une
# source d'une version precedente qu'on ne livre plus. Sans liste il ne purge rien -- une source
# orpheline vaut mieux qu'un dossier vide par accident.
$listeSources = ($sources.Name | Sort-Object) -join ';'
Write-Host "  add-on : $($sources.Count) sources + TdSwingEngine.cs"

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

$argsIscc = @("/DAppVersion=$Version", "/DPayload=$payload", "/DNtPayload=$ntPayload", "/DNtSources=$listeSources", "/DOutDir=$build", "/DBitlearnUrl=$BitlearnUrl")
if ($suffixe) { $argsIscc += "/DFileSuffix=$suffixe" }
& $iscc @argsIscc (Join-Path $PSScriptRoot 'TradeDeck.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC a echoue" }

$exe = Get-ChildItem $build -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$mo = [math]::Round($exe.Length / 1MB, 1)
Write-Host "`nInstallateur : $($exe.FullName) ($mo Mo)" -ForegroundColor Green
Write-Host "Serveur visé : $BitlearnUrl" -ForegroundColor Green

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
