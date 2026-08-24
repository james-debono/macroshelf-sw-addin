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


' Removes the SOLIDWORKS UI records this add-in has left behind under either
' name, in every SOLIDWORKS version key present. Runs on install, with
' SOLIDWORKS closed - which matters, because SOLIDWORKS rewrites these keys on
' exit and would put back anything deleted while it was running.
'
' Two separate stores, and both have to be cleared. Clearing only one was the
' bug found on 2026-08-24:
'
'   Custom API Toolbars\<id>   keyed by (add-in CLSID, group UserID), and holds
'                              the *title*. A group registering under an
'                              existing pair inherits the stored title - which
'                              is why MacroShelf, while it still shared
'                              MacroDeck's CLSID, drew a tab labelled MacroDeck.
'
'   CommandManager\<ctx>\Tab<n>  one per tab. SOLIDWORKS draws a tab for each of
'                              these regardless of whether any add-in claims it,
'                              and never removes them itself. They accumulate:
'                              an install/uninstall cycle leaves another behind.
'
' Tab records are matched on RefName being one of this product's own names, so
' nothing belonging to another add-in is touched. Removing the *current* name's
' records too is deliberate and safe: SOLIDWORKS recreates the live one when the
' add-in registers its command group on the next start, and it is what clears
' duplicates. The cost is that toolbar position resets, which CONVENTIONS s5
' already treats as disposable.
Function RemoveLegacyToolbars()
    On Error Resume Next
    RemoveLegacyToolbars = 1

    Dim reg, versions, i
    Set reg = GetObject("winmgmts:\\.\root\default:StdRegProv")
    If Err.Number <> 0 Then Exit Function

    reg.EnumKey HKEY_CURRENT_USER, "Software\SolidWorks", versions
    If Not IsArray(versions) Then Exit Function

    For i = 0 To UBound(versions)
        CleanToolbarStore reg, "Software\SolidWorks\" & versions(i) & _
                               "\User Interface\Custom API Toolbars"
        CleanTabStore reg, "Software\SolidWorks\" & versions(i) & _
                           "\User Interface\CommandManager\PartContext"
        CleanTabStore reg, "Software\SolidWorks\" & versions(i) & _
                           "\User Interface\CommandManager\AssyContext"
        CleanTabStore reg, "Software\SolidWorks\" & versions(i) & _
                           "\User Interface\CommandManager\DrwContext"
    Next

    Err.Clear
End Function


' Deletes toolbar records registered to the old MacroDeck CLSID. That GUID is
' ours, so no other vendor's toolbar can match however many are registered.
Sub CleanToolbarStore(reg, base)
    On Error Resume Next
    Dim entries, j, entry, moduleName
    reg.EnumKey HKEY_CURRENT_USER, base, entries
    If Not IsArray(entries) Then Exit Sub
    For j = 0 To UBound(entries)
        entry = base & "\" & entries(j)
        moduleName = ""
        reg.GetStringValue HKEY_CURRENT_USER, entry, "ModuleName", moduleName
        If StrComp(moduleName, LEGACY_CLSID, vbTextCompare) = 0 Then
            reg.DeleteKey HKEY_CURRENT_USER, entry
        End If
    Next
End Sub


' Deletes CommandManager tab records belonging to either of this product's
' names. Matching is exact, so a tab called "Macros" or belonging to another
' vendor is never touched.
Sub CleanTabStore(reg, base)
    On Error Resume Next
    Dim entries, j, entry, refName
    reg.EnumKey HKEY_CURRENT_USER, base, entries
    If Not IsArray(entries) Then Exit Sub
    For j = 0 To UBound(entries)
        entry = base & "\" & entries(j)
        refName = ""
        reg.GetStringValue HKEY_CURRENT_USER, entry, "RefName", refName
        If StrComp(refName, "MacroDeck", vbTextCompare) = 0 Or _
           StrComp(refName, "MacroShelf", vbTextCompare) = 0 Then
            reg.DeleteKey HKEY_CURRENT_USER, entry
        End If
    Next
End Sub


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
