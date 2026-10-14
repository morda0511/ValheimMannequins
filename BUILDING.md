# Building from source

For developers only. Players just install the mod from Thunderstore.

1. Install the .NET SDK and Valheim with BepInEx.
2. Create `Directory.Build.user.props` next to `Directory.Build.props` with your Valheim folder:
   ```xml
   <Project><PropertyGroup><GameDir>C:\Path\To\Valheim</GameDir></PropertyGroup></Project>
   ```
3. `dotnet build ValheimMannequins.sln -c Release`. The DLL is written to `out/ValheimMannequins/`.
