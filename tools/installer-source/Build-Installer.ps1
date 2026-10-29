$ErrorActionPreference='Stop'
$setup=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup'))
$facade=Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -File -Filter '*.cs' | ForEach-Object {$_.FullName})
& $compiler /nologo /codepage:65001 /langversion:5 /target:winexe /platform:x64 /optimize+ /debug- /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$facade" "/reference:$setup\Mono.Cecil.dll" "/win32manifest:$PSScriptRoot\src\app.manifest" "/out:$setup\LilithBridge-Installer.exe" @sources
if($LASTEXITCODE -ne 0){throw 'Own installer compilation failed. Product DLL was not rebuilt.'}
& $compiler /nologo /codepage:65001 /langversion:5 /target:winexe /main:LilithSetup.HandoffProgram /platform:x64 /optimize+ /debug- /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$facade" "/reference:$setup\Mono.Cecil.dll" "/win32manifest:$PSScriptRoot\src\app.manifest" "/out:$setup\LilithBridge-Handoff.exe" "$PSScriptRoot\handoff\HandoffProgram.cs" @sources
if($LASTEXITCODE -ne 0){throw 'Own handoff helper compilation failed. Product DLL was not rebuilt.'}
Copy-Item -LiteralPath (Join-Path $setup 'LilithBridge-Installer.exe.config') -Destination (Join-Path $setup 'LilithBridge-Handoff.exe.config') -Force
