# YogaNotes

A Windows note-taking application built with .NET 8 WPF. Pages combine a Word-style document editor (Times New Roman 12 pt) with pressure-sensitive stylus inking, shape snapping, and offline English/Bulgarian handwriting recognition.

## Storage

Notes and window settings are stored locally in `%APPDATA%\YogaNotes`.

## Run in development

```powershell
dotnet run
```

Or open `YogaNotes.slnx` in Visual Studio and press `F5`.

## Run tests

```powershell
dotnet test
```

## Regenerate app icon

```powershell
node scripts/make-icon.mjs
```

## Build portable Windows executable

```powershell
dotnet publish YogaNotes.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The standalone executable is written to `dist\YogaNotes.exe`.

## Create Desktop and Start Menu shortcuts

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-shortcut.ps1
```

If NSIS (`makensis`) is installed, you can also compile the Windows installer:

```powershell
makensis installer\YogaNotes.nsi
```
