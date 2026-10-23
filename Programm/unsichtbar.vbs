' Startet das Atomkraftwerk ohne sichtbares Fenster (wird von der Aufgabenplanung aufgerufen).
' Neustart ohne Doppelklick: Liegt beim Beenden eine Datei "neustart.flag" im Ordner, wartet dieses
' Skript, bis darin "los" steht (Dateien werden in der Zwischenzeit getauscht), und startet dann neu.
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
ordner = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = ordner
flagge = ordner & "\neustart.flag"

Function Lesen(pfad)
  On Error Resume Next
  Lesen = ""
  Set f = fso.OpenTextFile(pfad, 1)
  If Err.Number = 0 Then
    If Not f.AtEndOfStream Then Lesen = f.ReadAll
    f.Close
  End If
  Err.Clear
End Function

Do
  sh.Run """C:\Program Files\dotnet\dotnet.exe"" """ & ordner & "\Atomkraftwerk.dll""", 0, True
  If Not fso.FileExists(flagge) Then Exit Do
  For i = 1 To 300
    If InStr(LCase(Lesen(flagge)), "los") > 0 Then Exit For
    WScript.Sleep 2000
  Next
  On Error Resume Next
  fso.DeleteFile flagge, True
  On Error GoTo 0
  WScript.Sleep 1000
Loop
