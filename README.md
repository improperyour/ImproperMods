# ImproperMods

# Valheim Mods

LatencyLogger: measures and logs player latency.
SpeedTracker: measures how fast a character is moving

Drop it in both the server and clients plugin directory.

Requires BepInEx, because... what doesn't?

# Dev
To compile it pretty simple, just... compile.

EXCEPT .. you will almost 100% have to change line 14 in 
the .csproj file to point to your install of Valehim

Also, if you have not extracted all the DLLs from Valheim
you will need to do that as well.

Below is how you get those DLLs that is 100% copied from [RandyKnapps](https://github.com/RandyKnapp/ValheimMods/blob/main/ValheimModding-GettingStarted.md) most
excellent install guide for devs.  While I suggest you go read that, if you are an impatient soul then good luck.

* Get the Assembly Publicizer: [Link](https://github.com/elliotttate/Bepinex-Tools/releases) 
  * This one is the most important, it creates versions of the Valheim DLLs that have every class member and method made public, so you don’t have to do convoluted reflection stuff to get access to the private members, and your compiled mod will still link with the normal DLLs no problem.
  * NOTE: Run the game once after the publicizer is installed to generate the publicized DLLs! After you have the publicized DLLs, you can uninstall this mod and only re-install it when Valheim updates.



Used this to get assets:

https://valtools.org/wiki.php?page=Valheim-Unity-Project-Guide