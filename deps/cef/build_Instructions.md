All commands bellow are tested from cmd won't work on PowerShell unless otherwise noted

1\. check branch 5060 of cef last commit's date (2022-07-18 15:42:30 +0000)

2\. clone cef's wiki and find approximate date for correct instructions

3\. install VS2019 16.11.13+, Win 10.0.20348 SDK (not from vs but download indepently) 

4\. copy [obs branch](https://github.com/obsproject/cef/tree/5060-shared-textures) to own repo and create branch called 5060 (their name is not recognized by the scripts)

5\. create folder structure:
```
- C:\code
    - automate
    - chromium_git
```

6\. download [depo_tools.zip](https://storage.googleapis.com/chrome-infra/depot_tools.zip)

7\. unzip in c:\code\depot_tools using tool that preserves hidden files (don't copy from file explorer)

8\. update tools so it downlaods python and git
```
cd c:\code\depot_tools 
set DEPOT_TOOLS_WIN_TOOLCHAIN=0
set DEPOT_TOOLS_UPDATE=0 
update_depot_tools.bat
```
  
9\.  retrieve correct version of depot tools by setting commit date to last cefs commit date and checkout last depot tools before that date. 

From a git bash terminal:
```
export COMMIT_DATE="2022-07-18 15:42:30 +0000" 
git checkout $(git rev-list -n 1 --before="$COMMIT_DATE" main)
```

Or from PowerShell:
```
Set-Variable COMMIT_DATE "2022-07-18 15:42:30 +0000" 
git checkout $(git rev-list -n 1 --before="$COMMIT_DATE" main)
```

10\.  rename c:\depo_tools\.git to c:\depot_tools\.git_bak to make sure it won't update again

11\. Add the "c:\code\depot_tools" folder to your system PATH. For example, on Windows 10:
- Run the "SystemPropertiesAdvanced" command.
- Click the "Environment Variables..." button.
- Double-click on "Path" under "System variables" to edit the value.

12\. Download the [automate-git.py](https://bitbucket.org/chromiumembedded/cef/raw/master/tools/automate/automate-git.py) script to "c:\code\automate\automate-git.py".

13\. Clone the chromium source code and checkout the correct branch, in our case `103.0.5060.113`. The correct branch can be checked in the CHROMIUM_BUILD_COMPATIBILITY.txt file of the cef fork we are using in the `chromium_checkout`` key which in our case reads: 'chromium_checkout': 'refs/tags/103.0.5060.134',
```
cd c:\code\chromium_git\chromium
git clone https://chromium.googlesource.com/chromium/src.git
cd c:\code\chromium_git\chromium\src
git checkout 103.0.5060.113
```

14\. If any of the following steps fails, it's recomended to clean the chromium/src repository before retrying:

```
cd c:\code\chromium_git\chromium\src
git checkout .
git clean -ffd
```

15\. According to the [guide to build old revisions of chromium](https://chromium.googlesource.com/chromium/src/+/main/docs/building_old_revisions.md):
```
cd c:\code\chromium_git\chromium
set DEPOT_TOOLS_UPDATE=0
set DEPOT_TOOLS_WIN_TOOLCHAIN=0
gclient sync -D --force
```

16\. CEF allows 2 types of builds: for distribution or for development. If you want to just build the final packages follow steps 17, 18 and 19, for the development version jump to step 20:

17\. Create the "c:\code\chromium_git\update.bat" script with the following contents, substituting the repo for cef with the correct url.
```
set DEPOT_TOOLS_WIN_TOOLCHAIN=0
set DEPOT_TOOLS_UPDATE=0

REM For release
set GN_DEFINES=is_official_build=true use_thin_lto=false
REM Or with propietary codecs
REM set GN_DEFINES=is_official_build=true use_thin_lto=false proprietary_codecs=true ffmpeg_branding=Chrome
set GYP_MSVS_VERSION=2019
REM For tar.bz2 instead of zip
REM set CEF_ARCHIVE_FORMAT=tar.bz2 
python3 ..\automate\automate-git.py --download-dir=c:\code\chromium_git --depot-tools-dir=c:\code\depot_tools --minimal-distrib --client-distrib --force-clean --x64-build --with-pgo-profiles --branch="5060"  --no-depot-tools-update --no-chromium-update --url=https://github.com/arturoc/cef
```

18\. And run it:
```
cd c:\code\chromium_git\
update.bat
```

19\. The distribution files will now be in c:\code\chromium_git\src\cef\binary_distrib


20\. Alternatively CEF can be built for development using an update.bat version that downloads and prepares the projects but doesn't run the build or creates the distribution packages. For this create the "c:\code\chromium_git\update.bat" script with the following contents, substituting the repo for cef with the correct url.
```
set DEPOT_TOOLS_WIN_TOOLCHAIN=0
set DEPOT_TOOLS_UPDATE=0

REM For development
set GN_DEFINES=is_component_build=true
REM Or with propietary codecs
REM set GN_DEFINES=is_component_build=true proprietary_codecs=true ffmpeg_branding=Chrome
set GN_ARGUMENTS=--ide=vs2019 --sln=cef --filters=//cef/*
python ..\automate\automate-git.py --download-dir=c:\code\chromium_git --depot-tools-dir=c:\code\depot_tools --no-distrib --no-build --branch=5060 --no-depot-tools-update --no-chromium-update --url=https://github.com/arturoc/cef
```

21\. And run it:
```
cd c:\code\chromium_git\
update.bat
```

22\. Create the "c:\code\chromium_git\chromium\src\cef\create.bat" script with the following contents.

```
set GN_DEFINES=is_component_build=true
# Use vs2017 or vs2019 as appropriate.
set GN_ARGUMENTS=--ide=vs2019 --sln=cef --filters=//cef/*
set DEPOT_TOOLS_WIN_TOOLCHAIN=0
set DEPOT_TOOLS_UPDATE=0
call cef_create_projects.bat
```

23\. And run it to generate the visual studio and ninja projec files:
```
cd c:\code\chromium_git\chromium\src\cef
create.bat
```

24\. Now CEF and examples can be built including the OBS patches:
```
cd c:\code\chromium_git\chromium\src
ninja -C out\Debug_GN_x86 cef
```
Replace "Debug" with "Release" to generate a Release build instead of a Debug build. Replace “x86” with “x64” to generate a 64-bit build instead of a 32-bit build.

25\. Run the resulting cefclient sample application.

```
cd c:\code\chromium_git\chromium\src
out\Debug_GN_x86\cefclient.exe
```
