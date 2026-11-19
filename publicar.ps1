# Publica el servicio en una carpeta lista para copiar al servidor (Windows x64, no necesita instalar .NET)
param([string]$Salida = ".\publicado")
dotnet publish "$PSScriptRoot\KonSolutionsServicios.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Salida
Copy-Item "$PSScriptRoot\instalar.ps1","$PSScriptRoot\desinstalar.ps1" $Salida
Write-Host "Listo en $Salida. Copia esa carpeta al servidor y corre instalar.ps1 como Administrador."
