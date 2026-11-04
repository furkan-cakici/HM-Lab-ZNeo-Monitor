Set WshShell = CreateObject("WScript.Shell")
WshShell.Run "cmd /c """ & WshShell.CurrentDirectory & "\Start.bat""", 0, False
Set WshShell = Nothing
