# BuildMenu

A Valheim mod that adds a categorized filtering system to the build menu, making it easier to navigate large sets of build pieces from both vanilla and modded content.

## Development
### Requirements
* .NET Framework 4.6.2
* Local Valheim installation
* BepInEx + Jotunn assemblies

### Build

Update the Valheim install path if needed, then build:

```powershell
dotnet build BuildMenu.csproj -p:GamePath="C:\Path\To\Valheim"
```

The project may include a post-build step that copies the DLL into your local Valheim BepInEx/plugins directory. Adjust as needed.