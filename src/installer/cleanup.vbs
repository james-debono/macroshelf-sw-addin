' Custom actions for the MacroShelf installer.
'
' Two jobs, both things WiX cannot express declaratively because the keys and
' folders involved are not created by the installer and their names are not
' known until run time.
'
' Return values are always 1 (success). Neither of these is worth failing an
' install or an uninstall over: the worst case of doing nothing is a dormant
' registry key or a settings folder left behind.

Const HKEY_CURRENT_USER = &H80000001

' The CLSID the MacroDeck builds registered under. MacroShelf 0.8.0 moved to a
' new one so SOLIDWORKS would stop drawing the old tab (CONVENTIONS s9a). The
' old toolbar record survives that change, inert, because SOLIDWORKS only
' removes UI state when it is rewritten - not when an add-in stops existing.
'
' This GUID is ours. Matching on it means no other vendor's toolbar can be
' touched, however many are registered.
Const LEGACY_CLSID = "{1E9C2E64-7A5B-4C0D-9E3F-58A61D2B8C90}"


' Removes the dormant MacroDeck toolbar records, in every SOLIDWORKS version
' key present. Runs on install, with SOLIDWORKS closed - which matters, because
' SOLIDWORKS rewrites these keys on exit and would put back anything deleted
' while it was running.
Function RemoveLegacyToolbars()
    On Error Resume Next
    RemoveLegacyToolbars = 1

    Dim reg, versions, i, toolbars, j, base, entry, moduleName
    Set reg = GetObject("winmgmts:\\.\root\default:StdRegProv")
    If Err.Number <> 0 Then Exit Function

    reg.EnumKey HKEY_CURRENT_USER, "Software\SolidWorks", versions
    If Not IsArray(versions) Then Exit Function

    For i = 0 To UBound(versions)
        base = "Software\SolidWorks\" & versions(i) & _
               "\User Interface\Custom API Toolbars"
        toolbars = Empty
        reg.EnumKey HKEY_CURRENT_USER, base, toolbars
        If IsArray(toolbars) Then
            For j = 0 To UBound(toolbars)
                entry = base & "\" & toolbars(j)
                moduleName = ""
                reg.GetStringValue HKEY_CURRENT_USER, entry, "ModuleName", moduleName
                If StrComp(moduleName, LEGACY_CLSID, vbTextCompare) = 0 Then
                    reg.DeleteKey HKEY_CURRENT_USER, entry
                End If
            Next
        End If
    Next

    Err.Clear
End Function


' Removes %AppData%\MacroShelf on a genuine uninstall.
'
' The condition in Product.wxs excludes the uninstall half of a major upgrade,
' so upgrading never touches the user's library list or per-macro toggles. Only
' somebody deliberately removing MacroShelf loses them, which is what removing
' a program should mean - and it is what makes a clean reinstall genuinely
' clean, rather than silently inheriting the previous settings.
Function RemoveUserSettings()
    On Error Resume Next
    RemoveUserSettings = 1

    Dim shell, fso, folder
    Set shell = CreateObject("WScript.Shell")
    Set fso = CreateObject("Scripting.FileSystemObject")

    folder = shell.ExpandEnvironmentStrings("%APPDATA%") & "\MacroShelf"
    If fso.FolderExists(folder) Then
        fso.DeleteFolder folder, True
    End If

    Err.Clear
End Function
