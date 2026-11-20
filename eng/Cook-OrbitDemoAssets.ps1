[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GroundworkRenderingAssembly,

    [string]$Blender = 'blender',

    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../artifacts/orbit-cook-inputs')
)

$ErrorActionPreference = 'Stop'
$assembly = (Resolve-Path -LiteralPath $GroundworkRenderingAssembly).Path
$assemblySha = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant()
if ($assemblySha -cne '8ddce73b84c1dfeb9fd35ce26f0e14d7efd22297a973c764aa022006e0238bc2') {
    throw 'Use Groundwork.Rendering.dll from the verified 0.1.0-dev.44364420373.1 archive.'
}

$source = [IO.Path]::GetFullPath($SourceDirectory)
$destination = Join-Path $PSScriptRoot '../src/WorldsYetUnseen.Client/Assets/OrbitDemo'
$textureRoot = 'https://dl.polyhaven.org/file/ph-assets/Textures/png/1k'
$modelRoot = 'https://dl.polyhaven.org/file/ph-assets/Models/gltf'
$cc0 = 'CC0-1.0'

$textures = @(
    @{ Asset = 'rock_01'; Map = 'diff'; Cooked = 'diff'; Sha = '60404f5c3e505db17fc6501b3d0045e7aa6516e0baf08e81487f9a008a9c1dac' }
    @{ Asset = 'rock_01'; Map = 'nor_dx'; Cooked = 'normal'; Sha = '3f553c334f4c71479c11cac05086759dbb8250f5b1247ccbf403a188e31e6329' }
    @{ Asset = 'rock_01'; Map = 'rough'; Cooked = 'rough'; Sha = '6a99d084b3137ead2f6b76872d71148a8a912de9b7cf4f0509d7dc2515f9f938' }
    @{ Asset = 'rocks_ground_05'; Map = 'diff'; Cooked = 'diff'; Sha = 'eb25f853e38d4c43083921c330dce872df355071458ee0038a98088492e3f2d6' }
    @{ Asset = 'rocks_ground_05'; Map = 'nor_dx'; Cooked = 'normal'; Sha = 'b096aa1f0a1d491828b5437d784dbd953452bf0d581b82e096eb73c3aeeb1a0b' }
    @{ Asset = 'rocks_ground_05'; Map = 'rough'; Cooked = 'rough'; Sha = '47ad1ee100e98d1dbf62f9fe8060f617e92f7240fed36882ee9ca7da527d079b' }
)
$modelUrl = "$modelRoot/1k/boulder_01/boulder_01_1k.gltf"
$modelSha = '51487934db898b4c24072780a451e34eca5c02e3ada24a62bbc600bbc4019e92'
$modelBufferUrl = "$modelRoot/8k/boulder_01/boulder_01.bin"
$modelBufferSha = '7f5b06503d62bd95bfe9c6b8a354a5cc8ff04a4d271a8cf87a3e3125db4232d7'
$hdriUrl = 'https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/1k/toposcope_sunset_1k.hdr'
$hdriSha = '9cd21b7c45380368a87fe6a0251461faae5dd94a81b9d570762a74972d553f12'

function Get-Sha([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Source([string]$File, [string]$Url, [string]$Sha) {
    $path = Join-Path $source $File
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        Invoke-WebRequest -Uri $Url -OutFile $path
    }
    if ((Get-Sha $path) -cne $Sha) {
        throw "SHA-256 mismatch for $File."
    }
}

New-Item -ItemType Directory -Force -Path $source, $destination | Out-Null
foreach ($texture in $textures) {
    $name = "$($texture.Asset)_$($texture.Map)_1k.png"
    Get-Source $name "$textureRoot/$($texture.Asset)/$name" $texture.Sha
}
Get-Source 'boulder_01_1k.gltf' $modelUrl $modelSha
Get-Source 'boulder_01.bin' $modelBufferUrl $modelBufferSha
Get-Source 'toposcope_sunset_1k.hdr' $hdriUrl $hdriSha

$versionOutput = & $Blender --version
$blenderVersion = ($versionOutput | Select-Object -First 1).Trim()
if ($LASTEXITCODE -ne 0 -or $blenderVersion -ne 'Blender 5.2.2 LTS') {
    throw "Expected Blender 5.2.2 LTS, found '$blenderVersion'."
}

$cooker = Join-Path $PSScriptRoot 'Cook-OrbitDemoAssets.csproj'
& dotnet run --project $cooker --configuration Release "-p:OrbitCookerRenderingAssembly=$assembly" -- $source $destination
if ($LASTEXITCODE -ne 0) { throw 'The Groundwork texture cooker failed.' }

$boulderCooker = Join-Path $PSScriptRoot 'Cook-OrbitDemoAssets.py'
$gltf = Join-Path $source 'boulder_01_1k.gltf'
& $Blender --background --python-exit-code 1 --python $boulderCooker -- $gltf $destination
if ($LASTEXITCODE -ne 0) { throw 'Boulder mesh cooking failed.' }

$assets = @(
    foreach ($texture in $textures) {
        $file = "$($texture.Asset)_$($texture.Cooked).gwtx"
        $name = "$($texture.Asset)_$($texture.Map)_1k.png"
        [ordered]@{
            file = $file
            sourceUrl = "$textureRoot/$($texture.Asset)/$name"
            license = $cc0
            sourceSha256 = $texture.Sha
            cookedSha256 = Get-Sha (Join-Path $destination $file)
        }
    }
    foreach ($lod in 0, 1) {
        $file = "boulder_01_lod$lod.bin"
        $mesh = [IO.File]::ReadAllBytes((Join-Path $destination $file))
        [ordered]@{
            file = $file
            sourceUrl = $modelUrl
            license = $cc0
            sourceSha256 = $modelSha
            sourceBufferUrl = $modelBufferUrl
            sourceBufferSha256 = $modelBufferSha
            cookedSha256 = Get-Sha (Join-Path $destination $file)
            triangles = [BitConverter]::ToUInt32($mesh, 12) / 3
        }
    }
)
$manifest = [ordered]@{
    version = 1
    licenseUrl = 'https://polyhaven.com/license'
    cooking = [ordered]@{
        terrain = '512x512 RGBA8; 10 mips; 2x2 box with linear-light albedo and normalized DX normals; Groundwork.TextureCooker recipe 1'
        boulder = "$blenderVersion; weld at 0.00001 m, collapse decimation, centered Y-up WORB 1 mesh"
        groundworkPackage = '0.1.0-dev.44364420373.1'
        groundworkRenderingSha256 = $assemblySha
    }
    assets = $assets
    lightingReference = [ordered]@{
        sourceUrl = $hdriUrl
        license = $cc0
        sourceSha256 = $hdriSha
        use = 'Offline reference for warm low sunlight and cool sky/shadow contrast'
    }
}
$json = ($manifest | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), "$json`n")
