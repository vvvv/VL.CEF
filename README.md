# VL.CEF
Set of nodes to render websites VL. Based on the excellent .NET library [Chromium Embedded Framework (CEF)](https://bitbucket.org/chromiumembedded/cef/src/master/)

Try it with vvvv, the visual live-programming environment for .NET  
Download: http://visualprogramming.net

## Using the library with vvvv 2020.1
In order to use this library with VL you have to install the nuget that is available via nuget.org. For information on how to use nugets with VL, see [Managing Nugets](https://thegraybook.vvvv.org/reference/libraries/dependencies.html#manage-nugets) in the VL documentation. As described there you go to the commandline and then type:

    nuget install VL.CEF

Once the nuget is installed and referenced in your VL document you'll see the category "CEF" in the nodebrowser. 

Demos are available via the Help Browser!

## Using the library with vvvv >= 2020.2
Install the preview version of the package

    nuget install VL.CEF -pre -source nuget.org -source http://teamcity.vvvv.org/guestAuth/app/nuget/v1/FeedService.svc/
