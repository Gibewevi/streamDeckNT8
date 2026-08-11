# Enregistre (ou retire) la tâche planifiée qui lance TradeDeck à l'ouverture de session.
#
# Séparé de l'installateur parce que Inno Setup ne sait pas créer de tâche planifiée, et parce
# que l'opération doit tourner dans le contexte de l'utilisateur : une tâche enregistrée en
# administrateur ne se déclencherait pas à SA session.
#
# Appelé par l'installateur après la copie, et par le désinstalleur avant la suppression.

param(
  [Parameter(Mandatory = $true)][ValidateSet('install', 'uninstall', 'stop')][string]$Action,
  [string]$InstallDir = "$env:LOCALAPPDATA\TradeDeck",
  [string]$TaskName = 'TradeDeck'
)

$ErrorActionPreference = 'Stop'

<#
Arrête tout ce qui tourne depuis le dossier d'installation.

Deux processus, pas un : l'hôte (`node.exe`) **et le bridge** (`StreamDeckBridge.exe`), que le
superviseur lance à part. Ne fermer que l'hôte laissait le bridge tenir ses propres fichiers, et
l'installateur échouait à les remplacer — c'est exactement ce qui se produisait.

Arrêter le bridge ne lève aucune garantie de la macro de sécurité : son état, y compris
`lockedUntilUtc` et `tradeCount`, est écrit dans `%APPDATA%\StreamDeckTrader\safety-macro.json`
à chaque changement, et relu au redémarrage. Un verrou de six heures reste un verrou de six
heures après une réinstallation.
#>
function Stop-TradeDeckProcesses {
  param([string]$Dir)

  $cibles = @()

  $cibles += Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$Dir*" }

  # L'enveloppe `wscript.exe` qui porte l'hôte. Tuer Node suffirait — l'enveloppe rend la main dès
  # qu'il sort — mais l'ordre inverse est possible selon ce qui se termine en premier, et un
  # lanceur resté seul relancerait un hôte que l'on vient d'arrêter.
  $cibles += Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$Dir*" }

  # Filtré sur le chemin de l'exécutable : un bridge lancé depuis un autre dossier — une copie de
  # développement, par exemple — ne doit pas être abattu par l'installateur.
  $cibles += Get-CimInstance Win32_Process -Filter "Name='StreamDeckBridge.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) }

  foreach ($p in $cibles) {
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { }
  }

  if ($cibles.Count -eq 0) { return }

  # Windows libère les verrous de fichiers de façon asynchrone : rendre la main trop tôt
  # ferait échouer la copie sur un fichier encore verrouillé par un processus déjà mort.
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $restants = @($cibles | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($restants.Count -eq 0) { break }
  }
}

function Remove-TradeDeckTask {
  $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if (-not $existing) { return }
  try {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
  } catch {
    Write-Warning "Tache '$TaskName' non supprimee : $($_.Exception.Message)"
  }
}

# Appelé par l'installateur AVANT la copie des fichiers : à ce moment la tâche existe peut-être
# déjà et relancerait l'hôte entre l'arrêt et la copie.
if ($Action -eq 'stop') {
  $existante = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($existante) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
  Stop-TradeDeckProcesses -Dir $InstallDir
  return
}

if ($Action -eq 'uninstall') {
  Remove-TradeDeckTask
  # Sans cet arrêt, hôte et bridge gardent leurs fichiers verrouillés et la désinstallation
  # laisse un dossier à moitié vide.
  Stop-TradeDeckProcesses -Dir $InstallDir
  return
}

# Réenregistrement systématique : une mise à jour peut changer le chemin de l'exécutable ou les
# arguments, et une tâche existante conserverait silencieusement les anciens.
Remove-TradeDeckTask
# Ceinture et bretelles : la copie vient d'avoir lieu, mais un hôte relancé entre-temps
# tiendrait encore l'ancien binaire en mémoire.
Stop-TradeDeckProcesses -Dir $InstallDir

$node = Join-Path $InstallDir 'node.exe'
if (-not (Test-Path $node)) { throw "node.exe introuvable dans $InstallDir" }
$lanceur = Join-Path $InstallDir 'run-host.vbs'
if (-not (Test-Path $lanceur)) { throw "run-host.vbs introuvable dans $InstallDir" }

# Par `wscript.exe` et non `node.exe` directement : Node est une application console, et une tâche
# planifiée en session interactive lui alloue une fenêtre noire qui reste ouverte tant que
# TradeDeck tourne. `wscript` est un hôte fenêtré, il lance Node masqué.
# `//B` supprime les boîtes de dialogue d'erreur du moteur de script : personne n'est là pour les
# fermer, et une fenêtre modale bloquerait le démarrage jusqu'à la prochaine session.
#
# `$tacheAction` et NON `$action` : PowerShell ne distingue pas la casse, `$action` désignait donc
# le paramètre `$Action` du script — qui porte encore son `[ValidateSet('install','uninstall',
# 'stop')][string]`. Y affecter un objet MSFT_TaskExecAction levait une ValidationMetadataException
# à cette ligne exacte, et la tâche n'était JAMAIS enregistrée. Comme l'installateur appelle ce
# script en `runhidden`, l'erreur était invisible : l'installation se terminait normalement en
# laissant en place la tâche précédente, celle qui lance node.exe et ouvre une console.
$tacheAction = New-ScheduledTaskAction -Execute 'wscript.exe' `
  -Argument "//B //Nologo `"$lanceur`"" -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# Relance sur sortie non nulle : l'hôte s'arrête volontairement quand une autre instance occupe
# déjà le port, et il ne faut pas qu'une tâche morte laisse le deck éteint après un incident.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
  -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

# `-Force` remplace une tâche existante sans passer par la suppression, qui peut être refusée quand
# la tâche a été posée depuis une session élevée. `Set-ScheduledTask` en repli : il réécrit l'action
# d'une tâche que l'on n'a le droit ni de supprimer ni de réenregistrer. Sans ce repli, un poste
# gardait indéfiniment son ancienne tâche — exactement le cas qui a produit la fenêtre console.
# Aucun caractère non-ASCII dans les CHAÎNES de ce fichier — les commentaires, eux, sont libres.
# Windows PowerShell 5.1 lit un .ps1 sans BOM en ANSI : un tiret cadratin s'y décode en trois
# caractères dont U+201D, que PowerShell accepte comme guillemet fermant. La chaîne se terminait
# au milieu, et le script ne s'analysait plus. Vécu sur ce bloc précis.
try {
  Register-ScheduledTask -TaskName $TaskName -Action $tacheAction -Trigger $trigger `
    -Settings $settings -Principal $principal -Force -ErrorAction Stop | Out-Null
} catch {
  Write-Warning "Reenregistrement refuse ($($_.Exception.Message)) - mise a jour de la tache en place."
  try { Set-ScheduledTask -TaskName $TaskName -Action $tacheAction -Trigger $trigger -Settings $settings -ErrorAction Stop | Out-Null }
  catch { Write-Warning "Mise a jour en place refusee egalement : $($_.Exception.Message)" }
}

# Contrôle explicite : la tâche doit lancer `wscript.exe`. Une tâche laissée sur `node.exe` ouvre
# une fenêtre console à chaque ouverture de session, et rien d'autre ne le signalerait.
#
# Le cas qui rend ce contrôle nécessaire : une tâche « TradeDeck » créée un jour depuis une session
# élevée appartient à BUILTIN\Administrateurs, et l'utilisateur n'a plus dessus qu'un droit de
# lecture. L'installateur, non élevé par conception (l'élévation empêcherait la tâche de se
# déclencher à la session de l'utilisateur), ne peut alors ni la supprimer ni la réécrire : il
# échouait sans bruit et laissait en place l'ancienne, celle qui lance node.exe et ouvre une
# console. La boîte de dialogue est le seul moyen d'en informer quelqu'un — ce script est appelé
# en `runhidden`, sa sortie texte ne va nulle part.
$posee = (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).Actions.Execute
if ($posee -notlike '*wscript.exe*') {
  $remede = @"
TradeDeck n'a pas pu enregistrer son demarrage automatique.

Une tache planifiee '$TaskName' existe deja et n'appartient pas a votre compte : elle a ete creee
depuis une session administrateur, et l'installation ne peut pas la remplacer. Tant qu'elle est
la, TradeDeck demarre en ouvrant une fenetre de console.

Pour corriger : ouvrir un PowerShell EN ADMINISTRATEUR, lancer

    schtasks /delete /tn $TaskName /f

puis relancer cet installateur.
"@
  try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    [System.Windows.Forms.MessageBox]::Show($remede, 'TradeDeck', 'OK', 'Warning') | Out-Null
  } catch { }
  throw "La tache '$TaskName' lance toujours '$posee' au lieu de wscript.exe : l'hote afficherait une console."
}

# L'application Elgato occupe l'unique place plugin du bridge et se dispute le boîtier : son
# démarrage automatique doit céder la place. L'hôte la ferme aussi à chaque lancement.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($nom in 'Elgato Stream Deck', 'StreamDeck') {
  if (Get-ItemProperty -Path $run -Name $nom -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name $nom -ErrorAction SilentlyContinue
  }
}

Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
