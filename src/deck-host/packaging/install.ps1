<#
.SYNOPSIS
  Installe le TradeDeck et le rend autonome au démarrage de Windows.

.DESCRIPTION
  Remplace l'application Stream Deck d'Elgato. L'installation :
    - copie l'hôte, l'interface, le bridge et le moteur Node dans %LOCALAPPDATA% ;
    - crée une tâche planifiée qui démarre à l'ouverture de session, sans fenêtre,
      ET RELANCE l'hôte s'il ÉCHOUE — c'est ce qui remplace la relance automatique
      que l'application Elgato assurait pour le plugin. Le planificateur ne relance que
      sur code de sortie non nul : les plantages sont couverts (uncaughtException sort
      en 1), les arrêts délibérés ne le sont pas — et ne doivent pas l'être : port déjà
      occupé et SIGTERM sortent en 0 exprès, pour ne pas se battre avec une autre instance ;
    - désactive le démarrage automatique de Stream Deck, qui se disputerait le boîtier
      (l'application reste installée : elle seule met à jour le firmware) ;
    - pose un raccourci vers l'interface de configuration.

  Entièrement réversible par uninstall.ps1.

.NOTES
  Ne nécessite pas de droits administrateur : tout est fait dans le profil utilisateur.
#>
[CmdletBinding()]
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\TradeDeck",
  [string]$BitlearnUrl = 'https://bitlearn.fr',
  [string]$NodePath,
  [switch]$KeepElgatoAutostart
)

$ErrorActionPreference = 'Stop'
$TaskName = 'TradeDeck'
$Source   = Split-Path -Parent $PSScriptRoot   # …\src\deck-host

function Info($m) { Write-Host "  $m" }
function Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }

Step "1/6  Vérification des prérequis"
$node = if ($NodePath) { (Resolve-Path -LiteralPath $NodePath).Path } else { (Get-Command node -ErrorAction SilentlyContinue).Source }
if (-not $node) { throw "Node.js est introuvable dans le PATH. Installez-le puis relancez." }
$runtime = (& $node -p 'JSON.stringify({version:process.versions.node,arch:process.arch,platform:process.platform})') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $runtime.version -notmatch '^24\.' -or $runtime.arch -ne 'x64' -or $runtime.platform -ne 'win32') { throw 'Node 24 LTS Windows x64 requis.' }
Info "Node : $node ($($runtime.version))"
$BitlearnUrl = & (Join-Path $PSScriptRoot 'configure-server.ps1') -BitlearnUrl $BitlearnUrl -ValidateOnly
foreach ($d in @('dist', 'ui', 'node_modules', 'bridge')) {
  if (-not (Test-Path (Join-Path $Source $d))) {
    throw "Dossier '$d' absent. Lancez d'abord : npm install ; npm run build:all"
  }
}
Info "Sources complètes"

Step "2/6  Copie vers $InstallDir"
# L'hôte peut tourner : on l'arrête avant d'écraser ses fichiers.
& (Join-Path $PSScriptRoot 'register-task.ps1') -Action stop -InstallDir $InstallDir -TaskName $TaskName
Start-Sleep -Milliseconds 700
# The prerequisites succeeded and the old host has stopped. Only now archive an environment
# transition, leaving every journal line, cursor and safety lock unchanged.
& (Join-Path $PSScriptRoot 'configure-server.ps1') -BitlearnUrl $BitlearnUrl

New-Item -ItemType Directory -Force $InstallDir | Out-Null
foreach ($d in @('dist', 'ui', 'node_modules', 'bridge')) {
  # Destination explicite et purge préalable. « Copy-Item <dir> <dossier-existant> -Recurse »
  # imbrique (dist\dist) à la réinstallation, et -Force écrase sans jamais supprimer : un fichier
  # retiré des sources survivait indéfiniment dans l'installation.
  $dest = Join-Path $InstallDir $d
  $absoluteDest = [IO.Path]::GetFullPath($dest)
  $absoluteInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
  if (-not $absoluteDest.StartsWith($absoluteInstall, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination hors du dossier installation.' }
  if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
  Copy-Item (Join-Path $Source $d) $dest -Recurse -Force
}
Copy-Item (Join-Path $Source 'package.json') $InstallDir -Force
# Le moteur Node est embarqué : l'installation ne doit pas dépendre du PATH de la session.
Copy-Item $node (Join-Path $InstallDir 'node.exe') -Force
# Le lanceur silencieux doit être en place AVANT l'enregistrement de la tâche, qui refuse de
# s'enregistrer sans lui.
Copy-Item (Join-Path $PSScriptRoot 'run-host.vbs') $InstallDir -Force
$targetJson = ConvertTo-Json -InputObject $BitlearnUrl -Compress
[IO.File]::WriteAllText((Join-Path $InstallDir 'dist\release-target.js'), ('export const RELEASE_TARGET = ' + $targetJson + ';'), (New-Object Text.UTF8Encoding($false)))
Info "Copié ($([math]::Round((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum/1MB,1)) Mo)"

Step "3/6  Tâche planifiée (démarrage + relance automatique)"
# Délégué à `register-task.ps1`, qui est aussi ce qu'appelle l'installateur Inno. Ce script
# enregistrait auparavant SA propre tâche, lançant `node.exe` directement : Node est une
# application console, une tâche interactive lui alloue donc une fenêtre — noire, ou reprise par
# Windows Terminal — qui restait ouverte tant que TradeDeck tournait. Pire, sa vérification
# « déjà à jour » reconnaissait cette forme fautive comme la bonne et la conservait, si bien
# qu'aucune réinstallation ne la corrigeait.
#
# Deux enregistreurs pour une même tâche, c'est un seul de trop : celui qui n'est pas utilisé en
# production dérive sans que personne ne s'en aperçoive. Il n'en reste qu'un.
& (Join-Path $PSScriptRoot 'register-task.ps1') -Action install -InstallDir $InstallDir -TaskName $TaskName -KeepElgatoAutostart:$KeepElgatoAutostart
Info "Tâche '$TaskName' enregistrée via register-task.ps1 (au logon, sans fenêtre)"

Step "4/6  Démarrage automatique de Stream Deck"
if ($KeepElgatoAutostart) {
  Info "Laissé en place (option -KeepElgatoAutostart)"
} else {
  Info "Traité par register-task.ps1 à partir du chemin de l'application Elgato"
}

Step "5/6  Lanceur et raccourcis"
# Le lanceur démarre l'hôte s'il ne répond pas, puis ouvre l'interface. Un raccourci pointant
# directement sur l'URL afficherait une page morte quand la tâche a été arrêtée.
$launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TradeDeck.vbs'), [Text.Encoding]::UTF8)
if (-not $launcher.Contains('Const PAQUET = ""')) { throw 'Ancre du lanceur introuvable.' }
$launcher = $launcher.Replace('Const PAQUET = ""', 'Const PAQUET = "' + $BitlearnUrl + '"')
[IO.File]::WriteAllText((Join-Path $InstallDir 'TradeDeck.vbs'), $launcher, (New-Object Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Force (Join-Path $InstallDir 'assets') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'assets\TradeDeck.ico') (Join-Path $InstallDir 'assets') -Force

$sh = New-Object -ComObject WScript.Shell
$cibles = @(
  "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\TradeDeck.lnk",
  "$env:USERPROFILE\Desktop\TradeDeck.lnk"
)
foreach ($c in $cibles) {
  $lnk = $sh.CreateShortcut($c)
  $lnk.TargetPath = "$env:SystemRoot\System32\wscript.exe"
  $lnk.Arguments = """$InstallDir\TradeDeck.vbs"""
  $lnk.WorkingDirectory = $InstallDir
  $lnk.IconLocation = "$InstallDir\assets\TradeDeck.ico,0"
  $lnk.Description = "Ouvre la configuration TradeDeck (démarre l'hôte si besoin)"
  $lnk.Save()
}
Info "Bureau et menu Démarrer → « TradeDeck »"

Step "6/6  Démarrage"
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 6
$ok = $false
try { $ok = (Invoke-WebRequest 'http://127.0.0.1:8220' -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch {}
if ($ok) {
  Write-Host "`nInstallation terminée." -ForegroundColor Green
  Write-Host "  Interface  : http://127.0.0.1:8220"
  Write-Host "  Journaux   : %APPDATA%\StreamDeckTrader\logs\host-*.log"
  Write-Host "  Layout     : %APPDATA%\StreamDeckTrader\layout.json"
  Write-Host "`n  Fermez l'application Stream Deck si elle tourne encore : elle se dispute le boîtier."
} else {
  Write-Warning "L'hôte ne répond pas encore sur le port 8220. Consultez le journal du jour."
}
