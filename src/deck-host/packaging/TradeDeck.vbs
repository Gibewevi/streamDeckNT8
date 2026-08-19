' Lanceur TradeDeck — celui du raccourci Bureau et du menu Démarrer.
'
' S'assure que l'hôte tourne, puis ouvre la configuration **sur Bitlearn** : c'est Bitlearn qui
' édite désormais la disposition, l'hôte se contente de l'appliquer.
'
' En VBScript plutôt qu'en .cmd : aucune fenêtre de console ne s'ouvre.

' Sonde de vie, pas destination : l'hôte sert encore cette page, c'est le moyen le plus simple de
' savoir s'il répond avant d'envoyer l'utilisateur sur Bitlearn.
Const SONDE = "http://127.0.0.1:8220"

' Repli quand bitlearn.json est absent ou illisible. L'installateur écrit ce fichier avec le
' serveur pour lequel il a été construit ; la valeur ci-dessous est remplacée à la
' construction par `build-installer.ps1`, pour qu'un paquet de développement ne puisse pas
' retomber sur la production — le trader y arriverait sur un 404 sans rien qui le relie à
' l'installateur qu'il a lancé.
Const REPLI = "https://bitlearn.fr"

Set sh = CreateObject("WScript.Shell")

' L'application Elgato et TradeDeck ne peuvent pas coexister : son plugin occupe l'unique place
' plugin du bridge. L'hôte la ferme aussi à son démarrage, mais le lanceur sert justement quand
' l'hôte tourne déjà — il faut donc la fermer ici également.
sh.Run "taskkill /IM StreamDeck.exe /F /T", 0, True

' Démarre la tâche si l'hôte ne répond pas. Une tâche déjà en cours ignore la demande, et l'hôte
' refuse de son côté une seconde instance : rien ne peut être lancé en double.
If Not HoteRepond() Then
  sh.Run "schtasks /run /tn ""TradeDeck""", 0, True
  ' Laisse le temps d'ouvrir le port avant d'envoyer le navigateur : sans cette attente, on
  ' arrive sur Bitlearn avant que le poste ne se soit manifesté, et les voyants annoncent une
  ' panne qui n'existe pas.
  For i = 1 To 20
    WScript.Sleep 500
    If HoteRepond() Then Exit For
  Next
End If

sh.Run ServeurBitlearn() & "/tradedeck/configuration", 1, False

' Même source que l'hôte : `%APPDATA%\StreamDeckTrader\bitlearn.json`.
'
' Sans cette lecture, le raccourci pointait la production en dur pendant que l'hôte, lui, suivait
' le fichier. Sur un poste de développement les deux divergeaient : la synchronisation marchait
' vers le serveur local, et le raccourci ouvrait une page absente en production.
Function ServeurBitlearn()
  ServeurBitlearn = REPLI
  On Error Resume Next

  chemin = sh.ExpandEnvironmentStrings("%APPDATA%") & "\StreamDeckTrader\bitlearn.json"
  Set fso = CreateObject("Scripting.FileSystemObject")
  If Not fso.FileExists(chemin) Then Exit Function

  contenu = fso.OpenTextFile(chemin, 1).ReadAll()
  Set re = New RegExp
  re.Pattern = """url""\s*:\s*""([^""]+)"""
  Set m = re.Execute(contenu)
  If m.Count > 0 Then
    url = m(0).SubMatches(0)
    ' Une barre finale produirait « //tradedeck/configuration », soit un 404 sans rapport visible
    ' avec sa cause.
    Do While Right(url, 1) = "/"
      url = Left(url, Len(url) - 1)
    Loop
    If Len(url) > 0 Then ServeurBitlearn = url
  End If

  On Error GoTo 0
End Function

Function HoteRepond()
  HoteRepond = False
  On Error Resume Next
  Set http = CreateObject("MSXML2.XMLHTTP")
  http.Open "GET", SONDE, False
  http.Send
  If Err.Number = 0 And http.Status = 200 Then HoteRepond = True
  On Error GoTo 0
End Function
