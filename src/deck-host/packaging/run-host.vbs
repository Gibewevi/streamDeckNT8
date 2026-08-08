' Lanceur silencieux de l'hôte, appelé par la tâche planifiée.
'
' `node.exe` est une application **console** : lancée directement par une tâche planifiée en
' session interactive, Windows lui alloue une fenêtre noire qui reste ouverte tant que TradeDeck
' tourne. Inacceptable pour un service qui doit se faire oublier.
'
' `wscript.exe` est un hôte fenêtré, sans console. Il lance donc Node en style 0 — masqué.
'
' L'attente (`True`) est délibérée, et ce n'est pas un détail : sans elle ce script rendrait la
' main aussitôt, la tâche planifiée se croirait terminée, et sa relance sur échec ne couvrirait
' plus rien. En attendant, le code de sortie de Node devient celui de la tâche, et la relance
' automatique retrouve son sens.

Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

dossier = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dossier

commande = """" & dossier & "\node.exe"" dist\host.js"
code = sh.Run(commande, 0, True)

WScript.Quit code
