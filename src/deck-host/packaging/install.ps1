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
  [switch]$KeepElgatoAutostart
)

$ErrorActionPreference = 'Stop'
$TaskName = 'TradeDeck'
$Source   = Split-Path -Parent $PSScriptRoot   # …\src\deck-host

function Info($m) { Write-Host "  $m" }
function Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }

Step "1/6  Vérification des prérequis"
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw "Node.js est introuvable dans le PATH. Installez-le puis relancez." }
Info "Node : $node ($(node -v))"
foreach ($d in @('dist', 'ui', 'node_modules', 'bridge')) {
  if (-not (Test-Path (Join-Path $Source $d))) {
    throw "Dossier '$d' absent. Lancez d'abord : npm install ; npm run build:all"
  }
}
Info "Sources complètes"

Step "2/6  Copie vers $InstallDir"
# L'hôte peut tourner : on l'arrête avant d'écraser ses fichiers.
Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Where-Object { $_.CommandLine -like '*deck-host*' -or $_.CommandLine -like '*TradeDeck*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 700

New-Item -ItemType Directory -Force $InstallDir | Out-Null
foreach ($d in @('dist', 'ui', 'node_modules', 'bridge')) {
  # Destination explicite et purge préalable. « Copy-Item <dir> <dossier-existant> -Recurse »
  # imbrique (dist\dist) à la réinstallation, et -Force écrase sans jamais supprimer : un fichier
  # retiré des sources survivait indéfiniment dans l'installation.
  $dest = Join-Path $InstallDir $d
  if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
  Copy-Item (Join-Path $Source $d) $dest -Recurse -Force
}
Copy-Item (Join-Path $Source 'package.json') $InstallDir -Force
# Le moteur Node est embarqué : l'installation ne doit pas dépendre du PATH de la session.
Copy-Item $node (Join-Path $InstallDir 'node.exe') -Force
Info "Copié ($([math]::Round((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum/1MB,1)) Mo)"

Step "3/6  Tâche planifiée (démarrage + relance automatique)"
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'node.exe') `
                                  -Argument 'dist\host.js' -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# RestartCount/RestartInterval : filet contre les PLANTAGES (sortie non nulle). Un arrêt propre
# — port occupé, SIGTERM — sort en 0 et n'est volontairement pas relancé.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
              -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
              -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
  -Settings $settings -Principal $principal `
  -Description "TradeDeck — cockpit de trading NinjaTrader — pilote le Stream Deck sans l'application Elgato." | Out-Null
Info "Tâche '$TaskName' enregistrée (au logon, relance toutes les minutes si arrêt)"

Step "4/6  Démarrage automatique de Stream Deck"
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$backup = Join-Path $InstallDir 'elgato-autostart.bak'
$val = (Get-ItemProperty -Path $run -ErrorAction SilentlyContinue).'Elgato Stream Deck'
if ($KeepElgatoAutostart) {
  Info "Laissé en place (option -KeepElgatoAutostart)"
} elseif ($val) {
  Set-Content -Path $backup -Value $val -Encoding UTF8
  Remove-ItemProperty -Path $run -Name 'Elgato Stream Deck' -ErrorAction SilentlyContinue
  Info "Désactivé (valeur sauvegardée dans elgato-autostart.bak, restaurée par uninstall.ps1)"
  Info "L'application reste installée — elle seule met à jour le firmware du boîtier."
} else {
  Info "Aucun démarrage automatique Elgato trouvé"
}

Step "5/6  Lanceur et raccourcis"
# Le lanceur démarre l'hôte s'il ne répond pas, puis ouvre l'interface. Un raccourci pointant
# directement sur l'URL afficherait une page morte quand la tâche a été arrêtée.
Copy-Item (Join-Path $PSScriptRoot 'TradeDeck.vbs') $InstallDir -Force
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
