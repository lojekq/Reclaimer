# Development sandbox (07 §9, 00-shared §9.4): pwsh tools/sandbox.ps1 [-Reset]
# Makes C:\Demo\reclaimer-sandbox with the layouts Reclaimer knows: HF hub, Ollama (two manifests sharing a layer), npm, NuGet, pip, uv,
# a custom model folder and projects in git. Big files are placeholders from `fsutil file createnew` (~1.3 GB in all).
# The Debug build points OLLAMA_MODELS, HF_HUB_CACHE, NUGET_PACKAGES, npm_config_cache, PIP_CACHE_DIR, UV_CACHE_DIR here
# (Scan.UseSandboxInDebug) and its Quarantine refuses every path outside this folder.
param([switch]$Reset)
$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true
$S = 'C:\Demo\reclaimer-sandbox'
if ($Reset -and (Test-Path $S)) {
  if ((Get-Process Reclaimer -ErrorAction Ignore)) { throw 'close Reclaimer first' }
  Remove-Item -LiteralPath $S -Recurse -Force    # only this folder; there are no links inside to follow
}
if (Test-Path $S) { Write-Host "$S exists (use -Reset to rebuild)"; return }

function Folder($p) { New-Item -ItemType Directory -Force (Join-Path $S $p) | Out-Null; Join-Path $S $p }
function Text($p, $t) { $f = Join-Path $S $p; New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null; [IO.File]::WriteAllText($f, $t); $f }
function Big($p, [long]$mb) { $f = Join-Path $S $p; New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null; fsutil file createnew $f ($mb * 1MB) | Out-Null; $f }
function Old($path, [int]$days) {   # files and folders look untouched for $days
  $t = (Get-Date).AddDays(-$days)
  Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction Ignore | ForEach-Object { $_.LastWriteTime = $t; $_.LastAccessTime = $t }
  $i = Get-Item -LiteralPath $path -Force; $i.LastWriteTime = $t; $i.LastAccessTime = $t
}
function Sha($seed) { -join ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($seed)) | ForEach-Object { $_.ToString('x2') }) }

# Hugging Face hub: per-repo blobs; snapshots hold plain files (Windows without developer mode has no symlinks)
foreach ($r in @(@{ n = 'models--demo--tiny-llama'; mb = 220; days = 160 }, @{ n = 'models--demo--whisper-small'; mb = 90; days = 20 })) {
  $sha = Sha $r.n; $rev = (Sha "$($r.n)-rev").Substring(0, 40)
  Big "huggingface\hub\$($r.n)\blobs\$sha" $r.mb | Out-Null
  Text "huggingface\hub\$($r.n)\refs\main" $rev | Out-Null
  Big "huggingface\hub\$($r.n)\snapshots\$rev\model.safetensors" 1 | Out-Null
  Text "huggingface\hub\$($r.n)\snapshots\$rev\config.json" '{"model_type":"demo"}' | Out-Null
  Old (Join-Path $S "huggingface\hub\$($r.n)") $r.days
}
Text 'huggingface\xet\shard-cache\a.shard' 'x' | Out-Null

# Ollama: tinyllama:latest and tinyllama:q8 share the license layer; one orphan blob
$cfg1 = Sha 'cfg1'; $cfg2 = Sha 'cfg2'; $w1 = Sha 'weights1'; $w2 = Sha 'weights2'; $lic = Sha 'license'; $orphan = Sha 'orphan'
Big "ollama\models\blobs\sha256-$w1" 300 | Out-Null
Big "ollama\models\blobs\sha256-$w2" 150 | Out-Null
Big "ollama\models\blobs\sha256-$orphan" 40 | Out-Null
foreach ($b in $cfg1, $cfg2, $lic) { Text "ollama\models\blobs\sha256-$b" "{`"blob`":`"$b`"}" | Out-Null }
function Manifest($cfg, $w) { "{`"schemaVersion`":2,`"config`":{`"digest`":`"sha256:$cfg`",`"size`":20},`"layers`":[{`"digest`":`"sha256:$w`",`"size`":1},{`"digest`":`"sha256:$lic`",`"size`":1}]}" }
Text 'ollama\models\manifests\registry.ollama.ai\library\tinyllama\latest' (Manifest $cfg1 $w1) | Out-Null
Text 'ollama\models\manifests\registry.ollama.ai\library\tinyllama\q8' (Manifest $cfg2 $w2) | Out-Null
Old (Join-Path $S 'ollama\models') 130

# Package caches
Big 'npm-cache\_cacache\content-v2\sha512\ab\cdef' 60 | Out-Null
Text 'npm-cache\_cacache\index-v5\ab\cd' 'index' | Out-Null
Big 'nuget\packages\newtonsoft.json\13.0.3\newtonsoft.json.13.0.3.nupkg' 55 | Out-Null
Text 'nuget\v3-cache\x\list.dat' 'x' | Out-Null
Big 'pip\Cache\http-v2\a\b\c' 70 | Out-Null
Big 'uv\cache\wheels-v5\pypi\torch\torch.whl' 80 | Out-Null
Old (Join-Path $S 'npm-cache') 100; Old (Join-Path $S 'pip') 200

# Custom model folder (ComfyUI-style)
Big 'models\checkpoints\demo-sd15.safetensors' 160 | Out-Null
Big 'models\loras\tiny.safetensors' 12 | Out-Null   # under 100 MB: not a unit
Old (Join-Path $S 'models') 150

# Projects in git (dates of the past via GIT_*_DATE)
function Repo($name, [int]$days, [string]$ignore) {
  $d = Folder "projects\$name"
  if ($ignore) { [IO.File]::WriteAllText("$d\.gitignore", $ignore) }
  $env:GIT_AUTHOR_DATE = $env:GIT_COMMITTER_DATE = (Get-Date).AddDays(-$days).ToString('o')
  git -C $d init -q -b main
  git -C $d add -A
  git -C $d -c user.name=demo -c user.email=demo@example.com -c commit.gpgsign=false commit -q -m init
  Remove-Item Env:GIT_AUTHOR_DATE, Env:GIT_COMMITTER_DATE
  $d
}
Text 'projects\old-telegram-bot\package.json' '{"name":"old-telegram-bot"}' | Out-Null
Text 'projects\old-telegram-bot\index.js' 'console.log("bot")' | Out-Null
$d = Repo 'old-telegram-bot' 214 "node_modules/`n"
Big 'projects\old-telegram-bot\node_modules\telegraf\dist\index.js' 120 | Out-Null
Old "$d\node_modules" 214; Old "$d\index.js" 214; Old "$d\package.json" 214; Old "$d\.gitignore" 214; Old "$d\.git\index" 214

Text 'projects\rust-raytracer\Cargo.toml' "[package]`nname = `"raytracer`"" | Out-Null
$d = Repo 'rust-raytracer' 300 "/target`n"
Text 'projects\rust-raytracer\target\CACHEDIR.TAG' 'Signature: 8a477f597d28d172789f06886806bc55' | Out-Null
Big 'projects\rust-raytracer\target\debug\raytracer.exe' 90 | Out-Null
Get-ChildItem $d -Force | ForEach-Object { Old $_.FullName 300 }

Text 'projects\shop-api\ShopApi.csproj' '<Project Sdk="Microsoft.NET.Sdk" />' | Out-Null
$d = Repo 'shop-api' 120 "bin/`nobj/`n"
Big 'projects\shop-api\bin\Debug\net10.0\ShopApi.dll' 30 | Out-Null
Text 'projects\shop-api\obj\project.assets.json' '{}' | Out-Null
Big 'projects\shop-api\obj\Debug\cache.bin' 25 | Out-Null
Get-ChildItem $d -Force | ForEach-Object { Old $_.FullName 120 }

Text 'projects\fresh-app\package.json' '{"name":"fresh-app"}' | Out-Null
$d = Repo 'fresh-app' 2 "node_modules/`n"
Big 'projects\fresh-app\node_modules\react\index.js' 70 | Out-Null

Text 'projects\tracked-deps\package.json' '{"name":"tracked-deps"}' | Out-Null
Big 'projects\tracked-deps\node_modules\lib\index.js' 55 | Out-Null
$d = Repo 'tracked-deps' 400 ''                  # node_modules committed, not in .gitignore: never selectable
Get-ChildItem $d -Force | ForEach-Object { Old $_.FullName 400 }

Text 'projects\nogit-scraper\.venv\pyvenv.cfg' 'home = C:\Python314' | Out-Null
Big 'projects\nogit-scraper\.venv\Lib\site-packages\numpy\core.pyd' 65 | Out-Null
Text 'projects\nogit-scraper\main.py' 'print(1)' | Out-Null
Old (Join-Path $S 'projects\nogit-scraper') 180

$mb = [int]((Get-ChildItem $S -Recurse -File -Force | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "sandbox ready: $S ($mb MB)"
