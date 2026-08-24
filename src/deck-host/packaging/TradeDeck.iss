; Installateur Bitlearn TradeDeck — Inno Setup 6.
;
; Produit un unique .exe téléchargeable depuis Bitlearn. L'utilisateur double-clique, obtient un
; raccourci sur son bureau, et TradeDeck démarre à chaque ouverture de session. Plus aucune
; commande à taper.
;
; Construction : ne pas appeler ISCC directement — `build-installer.ps1` prépare d'abord une
; charge utile sans les dépendances de développement (26 Mo de TypeScript s'y trouvaient sinon).

#define AppName        "Bitlearn TradeDeck"
#define AppPublisher   "Bitlearn"
#define ExeBase        "BitlearnTradeDeck-Setup"

; Serveur Bitlearn que ce paquet vise. L'installateur l'écrit dans
; %APPDATA%\StreamDeckTrader\bitlearn.json — la seule source lue à la fois par l'hôte et par le
; raccourci du bureau. Sans elle, les deux retombent sur la production, quelle que soit la
; provenance du téléchargement.
;
; Ne pas surcharger à la main : `build-installer.ps1 -BitlearnUrl https://dev.bitlearn.fr` aligne
; aussi le repli du lanceur et le nom du fichier produit.
#ifndef BitlearnUrl
  #define BitlearnUrl "https://bitlearn.fr"
#endif

; Suffixe de nom pour un paquet qui ne vise pas la production. Deux .exe de même version pointant
; deux serveurs différents sont autrement impossibles à distinguer dans un dossier de
; téléchargements — et se tromper des deux mène à un 404 muet.
#ifndef FileSuffix
  #define FileSuffix ""
#endif

#define AppUrl BitlearnUrl + "/tradedeck"

#ifndef AppVersion
  #define AppVersion "0.29.0"
#endif

#ifndef Payload
  #define Payload "..\..\..\build\payload"
#endif

; Sources NinjaScript à déposer dans NinjaTrader. Hors de `Payload` à dessein : ce dernier est
; copié en bloc vers `{app}`, et ces fichiers n'ont rien à y faire — leur seule destination est
; `Documents\NinjaTrader 8\bin\Custom`.
#ifndef NtPayload
  #define NtPayload "..\..\..\build\ninjatrader"
#endif

; Noms des sources livrées, séparés par des points-virgules. Sert à retirer APRÈS la copie
; celles d'une version précédente qu'on ne livre plus. Vide, la purge ne s'exécute pas : mieux
; vaut une source orpheline qu'un dossier vidé parce qu'ISCC a été appelé sans cette liste.
#ifndef NtSources
  #define NtSources ""
#endif
#ifndef OutDir
  #define OutDir "..\..\..\build"
#endif

[Setup]
AppId={{8E4C1B77-3D2A-4C6E-9F31-TRADEDECK0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={localappdata}\TradeDeck
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\assets\TradeDeck.ico
OutputDir={#OutDir}
OutputBaseFilename={#ExeBase}-{#AppVersion}{#FileSuffix}
SetupIconFile=assets\TradeDeck.ico
WizardStyle=modern

; Installation par utilisateur, sans élévation. C'est ce qu'exige la tâche planifiée : enregistrée
; en administrateur, elle ne se déclencherait pas à la session de l'utilisateur. Cela évite aussi
; l'invite UAC, qui fait abandonner une partie des installations.
PrivilegesRequired=lowest

; LZMA2 au maximum : la charge utile est dominée par node.exe (91 Mo), très compressible.
Compression=lzma2/max
SolidCompression=yes

VersionInfoVersion={#AppVersion}

; Deux installateurs simultanés se disputeraient les mêmes fichiers et la même tâche planifiée.
; `SetupMutex` et non `AppMutex` : ce dernier cherche un mutex créé par l'APPLICATION, or l'hôte
; n'en crée aucun — son unicité vient de l'ouverture du port 8220. La directive était donc
; inerte, et le commentaire qui l'accompagnait annonçait une protection contre la rétrogradation
; qui n'existait nulle part. Elle est maintenant dans `InitializeSetup`.
SetupMutex=BitlearnTradeDeckSetup

; Tout le paquet est 64 bits : `node.exe` est un binaire x64, et le bridge est publié `win-x64`
; autonome depuis la 0.16.0. Le runtime .NET 8 qu'il transporte exige Windows 10 ou plus récent.
; Sans ces deux gardes, l'installation réussissait sur un Windows 32 bits ou un Windows 7, la
; tâche s'enregistrait, et rien ne démarrait jamais — un refus explicite vaut mieux.
ArchitecturesAllowed=x64compatible
MinVersion=10.0

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "register-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "assets\TradeDeck.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
; Deuxième copie, temporaire : l'arrêt des processus doit avoir lieu AVANT la copie, donc avant
; que la version installée du script n'existe. Sur une mise à jour, celle du dossier serait de
; surcroît l'ancienne.
Source: "register-task.ps1"; Flags: dontcopy

; L'intégration NinjaTrader. Sans elle le voyant « NinjaTrader » reste rouge quoi qu'il arrive :
; c'est l'add-on chargé DANS NinjaTrader qui ouvre la connexion vers le bridge, et rien d'autre
; ne le dépose. Les deux premiers clients payés ont buté là.
;
; `TdSwingEngine.cs` va dans `Indicators\` et NON avec l'add-on : `TrendEngine` le référence, les
; deux dossiers compilent dans le même `NinjaTrader.Custom.dll`, mais un second exemplaire sous
; `AddOns\StreamDeck\` lèverait un CS0101. Une compilation NinjaScript est tout ou rien : cet
; échec emporterait les indicateurs et stratégies personnels du trader.
Source: "{#NtPayload}\AddOns\StreamDeck\*.cs"; DestDir: "{code:DossierNinjaScript|AddOns\StreamDeck}"; \
  Flags: ignoreversion; Check: NinjaTraderPresent
Source: "{#NtPayload}\Indicators\TdSwingEngine.cs"; DestDir: "{code:DossierNinjaScript|Indicators}"; \
  Flags: ignoreversion uninsneveruninstall; Check: NinjaTraderPresent

[Icons]
; Le raccourci ouvre la configuration sur Bitlearn — l'interface locale n'est plus la référence.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\TradeDeck.vbs"; IconFilename: "{app}\assets\TradeDeck.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\TradeDeck.vbs"; IconFilename: "{app}\assets\TradeDeck.ico"; Tasks: desktopicon

[Run]
; -WindowStyle Hidden : l'enregistrement de la tâche ne doit pas faire clignoter une console.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\register-task.ps1"" -Action install -InstallDir ""{app}"""; \
  StatusMsg: "Enregistrement du démarrage automatique…"; Flags: runhidden waituntilterminated

; Lancement immédiat, sinon l'utilisateur devrait fermer sa session pour que TradeDeck démarre.
Filename: "{app}\TradeDeck.vbs"; Description: "Ouvrir TradeDeck"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
; Avant la suppression des fichiers : la tâche doit être retirée et l'hôte arrêté, sinon node.exe
; garde ses fichiers verrouillés et la désinstallation laisse un dossier à moitié vide.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\register-task.ps1"" -Action uninstall -InstallDir ""{app}"""; \
  RunOnceId: "RetirerTacheTradeDeck"; Flags: runhidden waituntilterminated

[Code]

{
  Clé de désinstallation posée par Inno pour ce paquet. Le GUID doit rester identique à `AppId`
  ci-dessus : c'est lui qui relie une installation à ses mises à jour. Duplication assumée
  plutôt que dérivée — `AppId` s'écrit avec une accolade échappée, et le faire passer par une
  définition ISPP est un excellent moyen de casser l'identité du produit sur tous les postes.

  Sous HKCU parce que l'installation est par utilisateur (`PrivilegesRequired=lowest`).
}
const
  CLE_DESINSTALLATION =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E4C1B77-3D2A-4C6E-9F31-TRADEDECK0001}_is1';

{
  Refuse d'écraser une version plus récente.

  Ce contrôle n'existait pas : un commentaire l'annonçait, appuyé sur `AppMutex`, qui ne fait
  rien de tel. Rien n'empêchait donc un 0.15.0 de s'installer par-dessus un 0.19.0 sans un mot,
  et de ramener au passage le bridge dépendant du framework — la panne des deux premiers clients.

  En cas de doute on laisse passer : version illisible, clé absente, produit jamais installé. Un
  garde-fou qui bloque une installation légitime coûte plus cher que celui qu'il remplace.
}
function InitializeSetup: Boolean;
var
  installee: String;
  vInstallee, vPaquet: Int64;
begin
  Result := True;

  if not RegQueryStringValue(HKEY_CURRENT_USER, CLE_DESINSTALLATION, 'DisplayVersion', installee) then Exit;
  if not StrToVersion(installee, vInstallee) then Exit;
  if not StrToVersion('{#AppVersion}', vPaquet) then Exit;
  if ComparePackedVersion(vPaquet, vInstallee) >= 0 then Exit;

  Result := False;
  if not WizardSilent then
    MsgBox('Ce poste utilise déjà TradeDeck ' + installee + ', plus récent que le '
           + '{#AppVersion} que vous lancez.' + #13#10#13#10
           + 'Installer une version antérieure retirerait des corrections déjà en place. Pour le '
           + 'faire quand même, désinstallez d''abord TradeDeck depuis les paramètres de Windows.',
           mbError, MB_OK);
end;

{
  Arrête l'hôte ET le bridge avant que le moindre fichier ne soit remplacé.

  `StreamDeckBridge.exe` est un processus séparé, lancé par le superviseur de l'hôte. Sans cet
  arrêt il garde ses propres fichiers ouverts, et l'installateur échoue à les écraser —
  « impossible de fermer StreamDeckBridge ». Fermer l'hôte seul ne suffit pas : il ne tue
  délibérément pas le bridge en s'arrêtant, celui-ci portant le verrou de la macro de sécurité.

  Rien n'est perdu au passage : l'état de la macro est écrit dans
  %APPDATA%\StreamDeckTrader\safety-macro.json à chaque changement et relu au redémarrage.
}
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('register-task.ps1');
  if not Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{tmp}\register-task.ps1') + '"'
        + ' -Action stop -InstallDir "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) then
    { L'échec du lancement de PowerShell n'est pas bloquant : si rien ne tournait, la copie
      passera de toute façon. Inno signalera lui-même un fichier verrouillé le cas échéant. }
    Log('Arret prealable impossible a lancer');
end;

{
  Fixe le serveur Bitlearn du poste, avant que quoi que ce soit ne démarre.

  L'hôte et le raccourci lisent tous deux %APPDATA%\StreamDeckTrader\bitlearn.json, et
  retombent sur la production quand il manque. Rien ne l'écrivait : un client servi par
  dev.bitlearn.fr installait donc un poste qui visait bitlearn.fr, où les pages TradeDeck
  n'existent pas. Il y arrivait sur un 404, sans le moindre indice que l'adresse était en
  cause. Constaté le 19/08/2026.

  Écrit en ssInstall et non en ssPostInstall : `register-task.ps1` lance la tâche à la
  fin de son enregistrement, et l'hôte lit sa cible une seule fois, au démarrage. Un fichier
  écrit après lui ne serait pris en compte qu'au redémarrage suivant.

  Le jeton d'appareil est effacé quand la cible change : il ne vaut que pour le serveur qui l'a
  émis, et l'hôte se considère appairé tant qu'il existe. Le garder produirait un
  poste silencieusement désynchronisé — 401 à chaque appel, aucune nouvelle demande
  d'appairage, et le deck qui continue de trader comme si de rien n'était. Le perdre coûte
  un clic au prochain démarrage.

  Le journal en attente n'est pas touché : ces lignes sont le travail du trader, pas un cache.
  Scellées avec la clé de l'ancien serveur, elles tomberont au palier non vérifiable sur
  le nouveau — dégradé, jamais perdu.
}

{ La valeur de "url" dans le fichier de configuration. Écrit par cet installateur, mais aussi
  à la main sur un poste de développement : on cherche la clé, pas une mise en forme. }
function UrlLue(const contenu: String): String;
var
  reste: String;
  p: Integer;
begin
  Result := '';
  p := Pos('"url"', contenu);
  if p = 0 then Exit;
  reste := Copy(contenu, p + 5, Length(contenu));
  p := Pos(':', reste);
  if p = 0 then Exit;
  reste := Copy(reste, p + 1, Length(reste));
  p := Pos('"', reste);
  if p = 0 then Exit;
  reste := Copy(reste, p + 1, Length(reste));
  p := Pos('"', reste);
  if p = 0 then Exit;
  Result := Copy(reste, 1, p - 1);
end;

{ La cible actuelle du poste. Fichier absent = production : c'est le repli codé en dur dans
  l'hôte comme dans le lanceur, donc bien ce que le poste visait jusqu'ici. Sans cette
  équivalence, la première mise à jour vers un installateur qui écrit le fichier
  désapparierait tous les postes déjà installés. }
function CibleActuelle(const chemin: String): String;
var
  brut: AnsiString;
  contenu, url: String;
begin
  Result := 'https://bitlearn.fr';
  if not FileExists(chemin) then Exit;
  if not LoadStringFromFile(chemin, brut) then Exit;
  contenu := brut;
  url := Trim(UrlLue(contenu));
  if url <> '' then Result := url;
end;

var
  RacineNinjaScript: String;

{
  La racine `bin\Custom` telle que NinjaTrader la publie lui-même.

  Elle vit dans `HKCU\Software\NinjaTrader, LLC\NinjaTrader\cmp<empreinte>`, une valeur par
  sous-clé : les noms de clés sont des empreintes, il faut donc les parcourir. Mesuré :
  476 sous-clés, 211 ms, et le résultat est mis en cache pour les dix-sept fichiers déposés.

  On le demande à la plateforme plutôt que de le déduire de Documents : ce n'est l'emplacement
  par défaut, rien n'oblige NinjaTrader à y rester, et se tromper de dossier ferait un dépôt
  invisible — l'add-on à côté de là où NinjaScript compile.

  Plusieurs correspondances = plusieurs instances NinjaTrader. On prend la première : rien ne
  permet de désigner la bonne, et le message de fin dira ce qui a été fait.
}
function RacineDeclareeParNinjaTrader: String;
var
  cles: TArrayOfString;
  i: Integer;
  valeur: String;
begin
  Result := '';
  if not RegGetSubkeyNames(HKEY_CURRENT_USER, 'Software\NinjaTrader, LLC\NinjaTrader', cles) then Exit;

  for i := 0 to GetArrayLength(cles) - 1 do
  begin
    if RegQueryStringValue(HKEY_CURRENT_USER,
        'Software\NinjaTrader, LLC\NinjaTrader\' + cles[i], 'PERSONAL_ROOT_BIN_CUSTOM', valeur) then
    begin
      Result := RemoveBackslashUnlessRoot(Trim(valeur));
      Exit;
    end;
  end;
end;

{
  Dossier NinjaScript du poste, résolu une fois pour toutes.

  Repli sur la constante Inno userdocs — et non sur %USERPROFILE% suivi de Documents en dur —
  quand la plateforme ne déclare rien, ce qui arrive lorsqu'elle est installée sans avoir
  jamais été lancée : ce dossier se redirige, OneDrive le fait par défaut sur beaucoup de
  machines neuves, et seule la constante suit la redirection.
}
function DossierNinjaScript(Param: String): String;
begin
  if RacineNinjaScript = '' then
  begin
    RacineNinjaScript := RacineDeclareeParNinjaTrader;
    if RacineNinjaScript <> '' then
      Log('NinjaScript : racine declaree par NinjaTrader -> ' + RacineNinjaScript)
    else
    begin
      RacineNinjaScript := ExpandConstant('{userdocs}\NinjaTrader 8\bin\Custom');
      { Trace dans le journal d'installation : les deux chemins coincident sur un poste par
        defaut, et l'on ne saurait pas autrement lequel a servi. }
      Log('NinjaScript : rien de declare, repli sur Documents -> ' + RacineNinjaScript);
    end;
  end;

  Result := AddBackslash(RacineNinjaScript) + Param;
end;

var
  NtVerifie: Boolean;
  NtDetecte: Boolean;

{
  NinjaTrader 8 est-il installé ?

  On ne crée jamais l'arborescence : la fabriquer sur un poste sans NinjaTrader y laisserait un
  faux dossier de plateforme, et NinjaTrader installé ensuite ne le lirait pas forcément.
  Absent, on saute le dépôt et on le DIT à la fin — un voyant rouge sans explication est
  exactement ce qu'on corrige ici.
}
function NinjaTraderPresent: Boolean;
begin
  if not NtVerifie then
  begin
    NtDetecte := DirExists(RemoveBackslashUnlessRoot(DossierNinjaScript('')));
    NtVerifie := True;
  end;
  Result := NtDetecte;
end;

{
  Retire les sources d'une version précédente que celle-ci ne livre plus.

  Après la copie, et non avant. Purger d'abord était plus simple, mais rien ne restaurait ces
  fichiers si l'installation échouait ou était annulée ensuite : Inno remet les siens, pas
  ceux-là, et le trader se retrouvait avec un NinjaTrader sans intégration, sans un mot.

  Une source orpheline n'est pas bénigne : une compilation NinjaScript est tout ou rien, donc un
  fichier qui ne compile plus emporte les indicateurs et stratégies du trader avec lui. C'est
  aussi ce qui rattrape une copie égarée de `TdSwingEngine.cs` déposée ici à la main, qui
  lèverait un CS0101 avec celle d'`Indicators\`.

  Seuls les `.cs` de NOTRE dossier sont examinés, et seuls ceux absents de la liste livrée sont
  retirés.
}
procedure PurgerOrphelinsAddOn;
var
  dossier, livrees, nom: String;
  rec: TFindRec;
begin
  livrees := Lowercase(Trim('{#NtSources}'));
  if livrees = '' then
  begin
    Log('NinjaScript : liste des sources livrees absente, purge ignoree');
    Exit;
  end;

  dossier := DossierNinjaScript('AddOns\StreamDeck');
  if not DirExists(dossier) then Exit;

  { Encadrée de séparateurs aux deux bouts, sinon `TrendEngine.cs` couvrirait `Engine.cs`. }
  livrees := ';' + livrees + ';';

  if not FindFirst(AddBackslash(dossier) + '*.cs', rec) then Exit;
  try
    repeat
      nom := Lowercase(rec.Name);
      if Pos(';' + nom + ';', livrees) = 0 then
      begin
        DeleteFile(AddBackslash(dossier) + rec.Name);
        Log('NinjaScript : source orpheline retiree -> ' + rec.Name);
      end;
    until not FindNext(rec);
  finally
    FindClose(rec);
  end;
end;

{
  L'hôte a-t-il répondu après l'installation ?

  `register-task.ps1` sonde le port 8220 pendant vingt secondes et écrit son verdict ici.
  `Start-ScheduledTask` est un ordre, pas une garantie : sans cette vérification l'installateur
  annonçait une réussite alors que l'hôte était mort au démarrage, et le client se retrouvait
  devant une page « aucun poste lié » sans la moindre piste.

  Marqueur absent — script d'une version antérieure, écriture refusée — on ne dit rien :
  alarmer à tort coûte plus cher que de se taire.
}
{
  NinjaTrader tournait-il pendant le dépôt ? Écrit par `register-task.ps1` dans le même marqueur.
}
function NinjaTraderTournait: Boolean;
var
  brut: AnsiString;
  texte: String;
begin
  Result := False;
  if not LoadStringFromFile(ExpandConstant('{app}\dernier-demarrage.txt'), brut) then Exit;
  texte := brut;
  Result := Pos('NinjaTrader=oui', texte) > 0;
end;

function HoteADemarre: Boolean;
var
  brut: AnsiString;
  texte: String;
begin
  Result := True;
  if not LoadStringFromFile(ExpandConstant('{app}\dernier-demarrage.txt'), brut) then Exit;
  texte := Trim(brut);
  Result := Copy(texte, 1, 2) <> 'KO';
end;

{ Ce que le trader doit savoir en partant, et rien de plus. }
procedure MessageDeFin;
begin
  if WizardSilent then Exit;

  { L'hôte d'abord : sans lui, l'état de l'intégration NinjaTrader n'a aucun intérêt. }
  if not HoteADemarre then
  begin
    MsgBox('TradeDeck est installé, mais il n''a pas démarré.' + #13#10#13#10
           + 'Rien ne répond sur le port 8220 vingt secondes après l''installation. Les deux '
           + 'causes habituelles : un antivirus qui a mis node.exe en quarantaine, ou un autre '
           + 'programme qui occupe déjà ce port.' + #13#10#13#10
           + 'Le journal de démarrage se trouve dans :' + #13#10
           + ExpandConstant('{userappdata}\StreamDeckTrader\logs'), mbError, MB_OK);
    Exit;
  end;

  if NinjaTraderPresent then
  begin
    { NinjaTrader ouvert surveille `bin\Custom` et recompile de lui-même, sans redémarrer.
      Fermé, il ne verra jamais ces fichiers arriver : il chargera son assemblage précédent au
      prochain lancement, et redémarrer n'y changera rien — c'est ce qu'on a demandé en vain à
      un client. Les deux situations n'appellent pas le même geste, donc pas le même message. }
    if NinjaTraderTournait then
      MsgBox('L''intégration NinjaTrader a été installée.' + #13#10#13#10
             + 'NinjaTrader était ouvert : il a recompilé l''add-on tout seul. Le voyant '
             + '« NinjaTrader » passe au vert dans les secondes qui suivent.', mbInformation, MB_OK)
    else
      MsgBox('L''intégration NinjaTrader a été installée. Une dernière étape :'
             + #13#10#13#10
             + '   Lancez NinjaTrader, puis  Control Center  →  New  →  NinjaScript Editor,'
             + #13#10
             + '   et appuyez sur F5 pour compiler.'
             + #13#10#13#10
             + 'Déposer les fichiers ne suffit pas : NinjaTrader réutilise sa dernière compilation '
             + 'tant qu''on ne lui en demande pas une nouvelle, et le relancer n''y change rien.'
             + #13#10#13#10
             + 'Plus simple la prochaine fois : ouvrez NinjaTrader AVANT de lancer cet '
             + 'installateur, il recompile alors de lui-même.', mbInformation, MB_OK);
  end
  else
    MsgBox('NinjaTrader 8 n''a pas été trouvé sur ce poste.' + #13#10#13#10 +
           'TradeDeck est installé et fonctionnel, mais son intégration NinjaTrader ne l''est ' +
           'pas : le voyant « NinjaTrader » restera rouge. Installez NinjaTrader 8, puis ' +
           'relancez cet installateur.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  dossier, config, precedente: String;
begin
  if CurStep = ssPostInstall then
  begin
    if NinjaTraderPresent then PurgerOrphelinsAddOn;
    MessageDeFin;
    Exit;
  end;
  if CurStep <> ssInstall then Exit;

  dossier := ExpandConstant('{userappdata}\StreamDeckTrader');
  config := AddBackslash(dossier) + 'bitlearn.json';
  precedente := CibleActuelle(config);

  if not ForceDirectories(dossier) then
  begin
    { Non bloquant : un poste sans fichier retombe sur la production, soit exactement l'état
      d'avant. Tracé dans le journal d'installation, rien n'est montré au trader. }
    Log('bitlearn.json : dossier d etat impossible a creer');
    Exit;
  end;

  if not SaveStringToFile(config, '{"url": "{#BitlearnUrl}"}' + #13#10, False) then
  begin
    Log('bitlearn.json : ecriture impossible');
    Exit;
  end;

  if CompareText(precedente, '{#BitlearnUrl}') <> 0 then
  begin
    { Les deux logements : le jeton a quitté le profil itinérant en 0.20.0, et un poste qui
      n'a pas encore été migré porte encore l'ancien. En oublier un le ferait revivre. }
    DeleteFile(AddBackslash(dossier) + 'device.json');
    DeleteFile(ExpandConstant('{localappdata}\StreamDeckTrader\device.json'));
    Log('Serveur Bitlearn : ' + precedente + ' -> {#BitlearnUrl}, le poste sera reapparie');
  end;
end;

[UninstallDelete]
; Le layout et les journaux vivent hors du dossier d'installation (%APPDATA%\StreamDeckTrader) et
; sont volontairement conservés : réinstaller ne doit pas effacer la configuration ni l'historique.
Type: filesandordirs; Name: "{app}\dist"
Type: filesandordirs; Name: "{app}\node_modules"
Type: filesandordirs; Name: "{app}\bridge"
Type: filesandordirs; Name: "{app}\ui"
; Écrit par `register-task.ps1`, donc inconnu du journal de désinstallation d'Inno.
Type: files; Name: "{app}\dernier-demarrage.txt"

; L'add-on est retiré de NinjaTrader : le laisser ferait compiler à chaque démarrage un add-on
; qui cherche un bridge désinstallé.
;
; `Indicators\TdSwingEngine.cs` reste, délibérément. Ce fichier seul ne coûte rien et
; compile sans rien exiger, alors que le supprimer casserait la compilation — tout ou rien,
; indicateurs du trader compris — s'il l'utilise dans un indicateur à lui.
Type: filesandordirs; Name: "{code:DossierNinjaScript|AddOns\StreamDeck}"
