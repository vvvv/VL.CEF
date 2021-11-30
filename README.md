# VL.CEF
Set of nodes to render websites in VL. Based on the excellent library [Chromium Embedded Framework (CEF)](https://bitbucket.org/chromiumembedded/cef/src/master/) and the .NET bindings [CefGlue](https://gitlab.com/xiliumhq/chromiumembedded/cefglue)

Try it with vvvv, the visual live-programming environment for .NET  
Download: http://visualprogramming.net

## Using the library
In order to use this library with VL you have to install the nuget that is available via nuget.org. For information on how to use nugets with VL, see [Managing Nugets](https://thegraybook.vvvv.org/reference/libraries/dependencies.html#manage-nugets) in the VL documentation. As described there you go to the commandline and then type:

    nuget install VL.CEF

Once the nuget is installed and referenced in your VL document you'll see the category "CEF" in the nodebrowser. 

Demos are available via the Help Browser!

## Using the library with previous vvvv releases
In case you're working with an older vvvv version you'll need to install a specific version of this library:
### 2020.1

    nuget install VL.CEF -version 0.0.3

### 2020.2

    nuget install VL.CEF -version 0.0.9-stride -pre -source nuget.org -source http://teamcity.vvvv.org/guestAuth/app/nuget/v1/FeedService.svc/

### 2021.3

    nuget install VL.CEF -version 0.1.1 -source nuget.org -source http://teamcity.vvvv.org/guestAuth/app/nuget/v1/FeedService.svc/
