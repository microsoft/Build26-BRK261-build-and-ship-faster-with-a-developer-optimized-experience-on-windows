# MarkItDown Desktop App

A native WinUI 3 desktop application that wraps the
[MarkItDown container service](../DemoContainer/) — drag-and-drop files, convert
them to Markdown, and copy or save the results. The app manages the full
container lifecycle using the
[Microsoft.WSL.Containers](https://www.nuget.org/packages/Microsoft.WSL.Containers)
C# SDK.

## Features

- **Drag & drop** any supported file onto the app to convert it to Markdown
- **File picker** for browsing and selecting documents
- **Copy / Save** converted Markdown with one click
- **Web Interface** tab embeds the container's built-in drag-drop web UI via WebView2
- **Container Log** tab shows real-time container output
- **Health monitoring** with status indicator in the navigation pane
- **Mica backdrop** and WinUI 3 controls for a modern Windows look

## Prerequisites

- Windows 10/11 with WSL 2 enabled
- .NET 8 SDK
- The `Microsoft.WSL.Containers` NuGet package (pulled from `C:\cDev\nuget` — see
  `NuGet.Config`)
- A pre-built container image saved as `Container\markitdown.tar`

## Building the container image

From the repo root, build the container image and export it as a tar:

```powershell
cd ..\DemoContainer
wslc build -t markitdown-service .
wslc save markitdown-service:latest -o ..\MarkItDownApp\Container\markitdown.tar
```

## Build & run

```powershell
dotnet build -p:Platform=x64
dotnet run -p:Platform=x64
```

Or open `MarkItDownApp.csproj` in Visual Studio 2022+ and press F5.

## How it works

1. On launch, the app creates a **WSL Container session** and loads the
   `markitdown-service` image from the bundled tar file.
2. A container is started with port 8000 mapped to the host.
3. The app polls the `/api/v1/health` endpoint until the FastAPI service is ready.
4. When you drop or pick a file, the app `POST`s it to `/api/v1/convert` and
   displays the returned Markdown.
5. The **Web Interface** tab opens `http://localhost:8000` in an embedded WebView2
   for the full browser-based experience.
6. On window close, the container and session are gracefully torn down.

## Project layout

```
MarkItDownApp/
├── App.xaml / App.xaml.cs          # Application entry point
├── MainWindow.xaml / .xaml.cs      # Main window with NavigationView UI
├── Services/
│   ├── ContainerService.cs         # WSL Container lifecycle management
│   └── MarkItDownClient.cs         # HTTP client for the REST API
├── Container/
│   └── markitdown.tar              # Pre-built container image (user-provided)
├── MarkItDownApp.csproj            # Project file (unpackaged WinUI 3)
├── NuGet.Config                    # NuGet package sources
└── app.manifest                    # Application manifest
```

## License

MIT — see [`LICENSE`](../LICENSE).
