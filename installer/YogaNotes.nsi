!include "MUI2.nsh"

Name "YogaNotes"
OutFile "..\dist\YogaNotes Setup 1.0.0.exe"
InstallDir "$LOCALAPPDATA\Programs\YogaNotes"
RequestExecutionLevel user

!define MUI_ICON "..\Assets\app-icon.ico"
!define MUI_UNICON "..\Assets\app-icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"
  File "..\dist\YogaNotes.exe"
  File "..\Assets\app-icon.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\YogaNotes"
  CreateShortcut "$SMPROGRAMS\YogaNotes\YogaNotes.lnk" "$INSTDIR\YogaNotes.exe" "" "$INSTDIR\app-icon.ico"
  CreateShortcut "$SMPROGRAMS\YogaNotes\Uninstall YogaNotes.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\YogaNotes.lnk" "$INSTDIR\YogaNotes.exe" "" "$INSTDIR\app-icon.ico"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\YogaNotes.lnk"
  Delete "$SMPROGRAMS\YogaNotes\YogaNotes.lnk"
  Delete "$SMPROGRAMS\YogaNotes\Uninstall YogaNotes.lnk"
  RMDir "$SMPROGRAMS\YogaNotes"
  Delete "$INSTDIR\YogaNotes.exe"
  Delete "$INSTDIR\app-icon.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
