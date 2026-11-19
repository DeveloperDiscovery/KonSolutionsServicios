# Genera la carpeta de instalacion: Instalador.exe + la subcarpeta Servicio (el servicio ya publicado). Windows x64, no necesita instalar .NET en el servidor.
# Uso (en una maquina con el SDK de .NET 9):   .\publicar.ps1 -Salida C:\temp\KonSolutionsInstalacion
param([string]$Salida = ".\KonSolutionsInstalacion")
$ErrorActionPreference = "Stop"
$raiz = Split-Path $PSScriptRoot -Parent   # la carpeta KonSolutionsServicios (el servicio esta un nivel arriba de este instalador)
if (Test-Path $Salida) { Remove-Item $Salida -Recurse -Force }
dotnet publish "$raiz\KonSolutionsServicios.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$Salida\Servicio"
if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion del servicio" }
dotnet publish "$PSScriptRoot\KonSolutionsInstalador.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Salida
if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion del instalador" }
Write-Host ""
Write-Host "Listo en $Salida. Copia esa carpeta al servidor y ejecuta Instalador.exe como Administrador."
