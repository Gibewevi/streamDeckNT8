' Lanceur TradeDeck.
'
' Ouvre l'interface de configuration, en s'assurant d'abord que l'hôte tourne. La tâche
' planifiée le démarre normalement à l'ouverture de session ; ce lanceur sert quand elle a
' été arrêtée, ou juste après une installation.
'
' En VBScript plutôt qu'en .cmd : aucune fenêtre de console ne s'ouvre.

Const URL = "http://127.0.0.1:8220"

Set sh = CreateObject("WScript.Shell")

' Démarre la tâche si l'hôte ne répond pas. Une tâche déjà en cours ignore la demande,
' et l'hôte refuse de son côté une seconde instance : rien ne peut être lancé en double.
If Not HoteRepond() Then
  sh.Run "schtasks /run /tn ""TradeDeck""", 0, True
  ' Laisse le temps d'ouvrir le port avant d'envoyer le navigateur sur une page morte.
  For i = 1 To 20
    WScript.Sleep 500
    If HoteRepond() Then Exit For
  Next
End If

sh.Run URL, 1, False

Function HoteRepond()
  HoteRepond = False
  On Error Resume Next
  Set http = CreateObject("MSXML2.XMLHTTP")
  http.Open "GET", URL, False
  http.Send
  If Err.Number = 0 And http.Status = 200 Then HoteRepond = True
  On Error GoTo 0
End Function
