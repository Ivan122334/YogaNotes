# YogaNotes — OneNote-Inspired Note Taking for Lenovo Yoga

YogaNotes is a lightweight, responsive Windows desktop note-taking application designed specifically for **Lenovo Yoga 2-in-1** convertibles. Built on .NET 8 WPF with hardware-accelerated Windows Inking, it delivers the signature Microsoft OneNote experience with true active stylus pressure sensitivity, palm rejection, and a flexible freeform canvas.

---

## ✨ Features

### 1. OneNote Information Architecture
* **Notebooks**: Create separate notebooks (e.g., *Personal*, *Work*, *Classes*).
* **Sections**: Color-coded tabs/sections within each notebook with vibrant OneNote-style pills.
* **Pages**: Chronological list of pages with timestamps and instant titles.
* **Auto-Save**: Everything autosaves as you write and switch between pages into `Documents\YogaNotesData`.

### 2. Freeform Canvas (Type Anywhere + Inking)
* **Type Anywhere**: Click anywhere on the page in **Type** mode or double-click anytime to spawn a floating, draggable, resizable text box.
* **Pressure-Sensitive Pen**: Hardware-accelerated ballpoint pen supporting pressure variation from Lenovo Active Pen.
* **Chisel Highlighter**: Semi-transparent, wide strokes for highlighting text or sketches.
* **Stroke Eraser**: Tap any stroke to erase.
* **Lasso Selection**: Select ink strokes and objects to move or rearrange your notes.
* **Color Chips & Stroke Sizes**: Quick access to 8 colors and preset thicknesses (Fine 2pt, Medium 4pt, Thick 8pt, Marker 16pt).
* **Paper Backgrounds**: Switch seamlessly between **Ruled / Lined Paper**, **Grid Paper**, and **Blank Sheet**.

### 3. Lenovo Yoga 2-in-1 Hardware Optimizations
* **Stylus Pressure Sensitivity**: Native `DrawingAttributes.IgnorePressure = false` and curve smoothing for fluid handwriting.
* **Automatic Eraser Tip**: Supports inverted stylus detection (`StylusDevice.Inverted`) so flipping the Lenovo Active Pen automatically activates the eraser.
* **Barrel Button Support**: Stylus side button dynamically activates the eraser on the fly.
* **Palm Rejection**: Hardware-level palm rejection provided by Windows Inking.
* **Tablet Mode Ready**: Touch-friendly 42px toolbar buttons and collapsible navigation sidebar (`☰` button) for full-screen tent or tablet sketching.
* **Export**: Export any page directly to high-resolution PNG image.

---

## 🚀 How to Run

### Quick Launch
Double-click `Run-YogaNotes.bat` in this folder, or launch the executable directly:
```powershell
.\bin\Debug\net8.0-windows\YogaNotes.exe
```

### Build & Run from Source
```powershell
dotnet build
dotnet run
```

---

## 📁 Storage & Data Location

Your notes are saved in your user Documents folder:
* `C:\Users\<Username>\Documents\YogaNotesData\notebooks.json` (Notebook & Section structure)
* `C:\Users\<Username>\Documents\YogaNotesData\Pages\<PageId>.json` (Page contents, ink strokes, text boxes, and paper styles)

This makes your notes easy to back up, sync, or export.
