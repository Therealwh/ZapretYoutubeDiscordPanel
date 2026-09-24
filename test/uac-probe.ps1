$ErrorActionPreference = 'SilentlyContinue'
$sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
"UAC_EnableLUA=$((Get-ItemProperty $sys).EnableLUA)"
"UAC_Consent=$((Get-ItemProperty $sys).ConsentPromptBehaviorAdmin)"
"UAC_SecureDesktop=$((Get-ItemProperty $sys).PromptOnSecureDesktop)"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
"ShellIsAdmin=$isAdmin"
try { net session *>$null; "NetSession=OK (elevated)" } catch { "NetSession=FAIL (not elevated)" }
$exe = 'C:\Program Files\YoutubeDiscordPanel\YoutubeDiscordPanel.exe'
$f = Get-Item $exe
"Installed: $($f.LastWriteTime) / $($f.Length) bytes"
$s = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($exe))
$i = $s.IndexOf('requestedExecutionLevel')
if ($i -ge 0) { "InstalledManifest: " + ($s.Substring($i, 80) -replace '[^\x20-\x7e]', '.') } else { "InstalledManifest: NOT FOUND (default asInvoker)" }
$d = 'C:\Zapret_panel\YoutubeDiscordPanel\dist\win-unpacked\YoutubeDiscordPanel.exe'
$df = Get-Item $d
"Dist: $($df.LastWriteTime) / $($df.Length) bytes"
$s2 = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($d))
$i2 = $s2.IndexOf('requestedExecutionLevel')
if ($i2 -ge 0) { "DistManifest: " + ($s2.Substring($i2, 80) -replace '[^\x20-\x7e]', '.') } else { "DistManifest: NOT FOUND (default asInvoker)" }
