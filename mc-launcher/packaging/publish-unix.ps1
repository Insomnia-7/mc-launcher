#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,[string]$OutputDirectory='',[string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
$appRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $appRoot "dist/$Runtime"}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$staging=Join-Path $appRoot ('scratch/publish-'+$Runtime+'-'+[Guid]::NewGuid().ToString('N'))
& $Dotnet publish (Join-Path $appRoot 'src/Launcher.Desktop/Launcher.Desktop.csproj') -c Release -r $Runtime --self-contained true -p:PublishProfile=UnixLean -p:DebugType=none -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -o $staging
if($LASTEXITCODE -ne 0){throw 'Unix publish failed.'}
$files=@(Get-ChildItem -LiteralPath $staging -File -Recurse|Where-Object {$_.Extension -ne '.pdb'})
if($files.Count -ne 1 -or $files[0].Name -ne 'MCLauncher.Desktop'){throw 'Expected a single native apphost.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$version=([xml](Get-Content (Join-Path $appRoot 'src/Launcher.Desktop/Launcher.Desktop.csproj') -Raw)).Project.PropertyGroup.Version
if($Runtime.StartsWith('linux-')){
 $payload=Join-Path $OutputDirectory 'payload';New-Item -ItemType Directory -Path $payload -Force|Out-Null
 Copy-Item -LiteralPath $files[0].FullName -Destination (Join-Path $payload 'Fangyu')
 & chmod 755 (Join-Path $payload 'Fangyu');if($LASTEXITCODE -ne 0){throw 'chmod failed'}
 Copy-Item -LiteralPath (Join-Path $appRoot 'src/Launcher.Desktop/Assets/Brand/fangyu.png') -Destination $payload
 [IO.File]::WriteAllText((Join-Path $payload 'fangyu.desktop'),"[Desktop Entry]`nType=Application`nName=方隅`nExec=Fangyu`nIcon=fangyu`nTerminal=false`nCategories=Game;`n")
 $archive=Join-Path $OutputDirectory "Fangyu-$version-$Runtime.tar.gz"
 & tar -czf $archive -C $payload .;if($LASTEXITCODE -ne 0){throw 'tar failed'}
}else{
 if(!$IsMacOS){throw 'macOS bundle signing requires a macOS host.'}
 $bundle=Join-Path $OutputDirectory '方隅.app';$contents=Join-Path $bundle 'Contents';$mac=Join-Path $contents 'MacOS';$resources=Join-Path $contents 'Resources'
 New-Item -ItemType Directory -Path $mac,$resources -Force|Out-Null
 Copy-Item -LiteralPath $files[0].FullName -Destination (Join-Path $mac 'Fangyu');& chmod 755 (Join-Path $mac 'Fangyu')
 $iconset=Join-Path $staging 'fangyu.iconset';New-Item -ItemType Directory -Path $iconset|Out-Null
 foreach($size in @(16,32,128,256,512)){foreach($scale in @(1,2)){$pixels=$size*$scale;$suffix=if($scale -eq 2){'@2x'}else{''}; & sips -z $pixels $pixels (Join-Path $appRoot 'src/Launcher.Desktop/Assets/Brand/fangyu.png') --out (Join-Path $iconset "icon_${size}x${size}$suffix.png")|Out-Null;if($LASTEXITCODE -ne 0){throw 'Icon conversion failed.'}}}
 & iconutil -c icns $iconset -o (Join-Path $resources 'fangyu.icns');if($LASTEXITCODE -ne 0){throw 'icns failed'}
 $numeric=$version.Split('-')[0]
 [IO.File]::WriteAllText((Join-Path $contents 'Info.plist'),@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>方隅</string><key>CFBundleDisplayName</key><string>方隅</string>
<key>CFBundleIdentifier</key><string>org.fangyu.launcher</string><key>CFBundleExecutable</key><string>Fangyu</string>
<key>CFBundlePackageType</key><string>APPL</string><key>CFBundleIconFile</key><string>fangyu.icns</string>
<key>CFBundleShortVersionString</key><string>$numeric</string><key>CFBundleVersion</key><string>$numeric</string>
<key>NSHighResolutionCapable</key><true/><key>LSMinimumSystemVersion</key><string>15.0</string>
</dict></plist>
"@)
 & codesign --force --deep --sign - $bundle;if($LASTEXITCODE -ne 0){throw 'Ad-hoc bundle signing failed.'}
 & codesign --verify --deep --strict $bundle;if($LASTEXITCODE -ne 0){throw 'Bundle signature verification failed.'}
 $archive=Join-Path $OutputDirectory "Fangyu-$version-$Runtime.zip"
 & ditto -c -k --keepParent $bundle $archive;if($LASTEXITCODE -ne 0){throw 'Bundle archive failed.'}
}
Get-FileHash -LiteralPath $archive -Algorithm SHA256
