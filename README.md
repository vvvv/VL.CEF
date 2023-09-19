# VL.CEF
Set of nodes to render websites in VL. Based on the excellent library [CEF](https://bitbucket.org/chromiumembedded/cef/src/master/) (Version 103.0.0) with [OBS patch](https://github.com/obsproject/cef/tree/5060-shared-textures) applied and the .NET bindings [CefGlue](https://gitlab.com/xiliumhq/chromiumembedded/cefglue)

Try it with vvvv, the visual live-programming environment for .NET  
Download: http://visualprogramming.net

## Using the library
In order to use this library with VL you have to install the nuget that is available via nuget.org. For information on how to use nugets with VL, see [Managing Nugets](https://thegraybook.vvvv.org/reference/libraries/dependencies.html#manage-nugets) in the VL documentation. As described there you go to the commandline and then type:

    nuget install VL.CEF.Stride
    
and/or
    
    nuget install VL.CEF.Skia

## Troubleshooting
If you don't see any output, this is probably because your PC has multiple GPUs. In this case you'll need to manually tell the chromium process to run on the same GPU that vvvv is running on. You do this by [assigning GPU preference to a program](https://www.ghacks.net/2021/10/29/how-to-assign-graphics-performance-preferences-to-windows-11-programs/). 

You'll find the chromium process like this:
- Quad menu -> Manage NuGets -> Show Installed
- Navigate to VL.CEF.x.y.z\renderer and choose "VL.CEF.Renderer.exe"
