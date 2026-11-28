param([string]$Version = "0.55.7")
$ErrorActionPreference = "Stop"
$dist = Join-Path (Split-Path $PSScriptRoot) "dist"
$zip = Join-Path $dist "Umamusume-companion-$Version.zip"
$out = Join-Path $dist "Umamusume-companion-$Version-Windows.exe"
if (!(Test-Path -LiteralPath $zip)) { throw "Missing release archive: $zip" }
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ "/out:$out" "/resource:$zip,package.zip" /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot "Program.cs")
if ($LASTEXITCODE) { throw "Installer build failed." }
